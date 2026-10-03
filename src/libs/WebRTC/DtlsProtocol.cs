using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Authentication;

namespace tryAGI.WebRTC;

internal static class DtlsProtocol
{
    internal const ushort Version = 0xFEFD;
    internal const int HeaderLength = 13;
    internal const int HandshakeHeaderLength = 12;
    internal const ulong MaximumSequence = (1UL << 48) - 1;

    internal static byte[] Handshake(byte type, ushort sequence, ReadOnlySpan<byte> body, int offset = 0, int? fragmentLength = null)
    {
        var size = fragmentLength ?? body.Length;
        var bytes = new byte[12 + size];
        bytes[0] = type;
        Write24(bytes.AsSpan(1), body.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), sequence);
        Write24(bytes.AsSpan(6), offset);
        Write24(bytes.AsSpan(9), size);
        body.Slice(offset, size).CopyTo(bytes.AsSpan(12));
        return bytes;
    }

    internal static int Read24(ReadOnlySpan<byte> bytes) => (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
    internal static void Write24(Span<byte> bytes, int value)
    { bytes[0] = (byte)(value >> 16); bytes[1] = (byte)(value >> 8); bytes[2] = (byte)value; }
    internal static ulong Read48(ReadOnlySpan<byte> bytes) =>
        ((ulong)BinaryPrimitives.ReadUInt16BigEndian(bytes) << 32) | BinaryPrimitives.ReadUInt32BigEndian(bytes[2..]);
    internal static void Write48(Span<byte> bytes, ulong value)
    { BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)(value >> 32)); BinaryPrimitives.WriteUInt32BigEndian(bytes[2..], (uint)value); }

    internal static byte[] Record(byte type, ushort epoch, ulong sequence, ReadOnlySpan<byte> body)
    {
        if (sequence > MaximumSequence || body.Length > ushort.MaxValue) throw new InvalidOperationException("DTLS record limit reached.");
        var result = new byte[13 + body.Length];
        result[0] = type;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1), Version);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(3), epoch);
        Write48(result.AsSpan(5), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(11), (ushort)body.Length);
        body.CopyTo(result.AsSpan(13));
        return result;
    }

    internal static byte[] Prf(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> label, ReadOnlySpan<byte> seed, int size)
    {
        var input = new byte[label.Length + seed.Length];
        label.CopyTo(input); seed.CopyTo(input.AsSpan(label.Length));
        var result = new byte[size];
        Span<byte> a = stackalloc byte[32];
        Span<byte> nextA = stackalloc byte[32];
        Span<byte> block = stackalloc byte[32];
        var round = new byte[32 + input.Length];
        try
        {
            HMACSHA256.HashData(secret, input, a);
            input.CopyTo(round, 32);
            for (var offset = 0; offset < size; offset += 32)
            {
                a.CopyTo(round);
                HMACSHA256.HashData(secret, round, block);
                block[..Math.Min(32, size - offset)].CopyTo(result.AsSpan(offset));
                HMACSHA256.HashData(secret, a, nextA); nextA.CopyTo(a);
            }
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(round);
            CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(nextA); CryptographicOperations.ZeroMemory(block);
        }
    }
}

internal sealed class TlsWriter
{
    private readonly List<byte> _bytes = [];
    internal void U8(int value) => _bytes.Add((byte)value);
    internal void U16(int value) { U8(value >> 8); U8(value); }
    internal void U24(int value) { U8(value >> 16); U16(value); }
    internal void Bytes(ReadOnlySpan<byte> bytes) { foreach (var value in bytes) _bytes.Add(value); }
    internal void Vector8(ReadOnlySpan<byte> bytes) { U8(bytes.Length); Bytes(bytes); }
    internal void Vector16(ReadOnlySpan<byte> bytes) { U16(bytes.Length); Bytes(bytes); }
    internal void Extension(ushort type, ReadOnlySpan<byte> bytes) { U16(type); Vector16(bytes); }
    internal byte[] ToArray() => _bytes.ToArray();
}

internal ref struct TlsReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> _bytes = bytes;
    internal readonly int Remaining => _bytes.Length;
    internal ReadOnlySpan<byte> Read(int length)
    {
        if (length < 0 || length > _bytes.Length) throw new InvalidDataException("Truncated DTLS handshake.");
        var result = _bytes[..length]; _bytes = _bytes[length..]; return result;
    }
    internal byte U8() => Read(1)[0];
    internal ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Read(2));
    internal int U24() => DtlsProtocol.Read24(Read(3));
    internal ReadOnlySpan<byte> Vector8() => Read(U8());
    internal ReadOnlySpan<byte> Vector16() => Read(U16());
    internal readonly void End() { if (Remaining != 0) throw new InvalidDataException("Unexpected DTLS handshake bytes."); }
}

internal sealed class DtlsRecordCipher : IDisposable
{
    private readonly object _gate = new();
    private readonly AesGcm _gcm;
    private readonly byte[] _iv;
    private ulong _sendSequence, _highest, _seen, _decryptAttempts;
    // Conservative per-key GCM invocation budget; reconnect before exhaustion.
    private const ulong MaximumInvocations = 1UL << 24;
    private bool _initialized, _disposed;

    internal DtlsRecordCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    { _gcm = new AesGcm(key, 16); _iv = iv.ToArray(); }

    internal byte[] Encrypt(byte type, ReadOnlySpan<byte> plaintext)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sendSequence >= MaximumInvocations) throw new InvalidOperationException("DTLS key exhausted.");
            var sequence = _sendSequence++;
            var packet = DtlsProtocol.Record(type, 1, sequence, new byte[plaintext.Length + 24]);
            packet.AsSpan(3, 8).CopyTo(packet.AsSpan(13, 8));
            Span<byte> nonce = stackalloc byte[12];
            _iv.CopyTo(nonce); packet.AsSpan(13, 8).CopyTo(nonce[4..]);
            Span<byte> aad = stackalloc byte[13];
            packet.AsSpan(3, 8).CopyTo(aad); aad[8] = type;
            packet.AsSpan(1, 2).CopyTo(aad[9..]);
            BinaryPrimitives.WriteUInt16BigEndian(aad[11..], (ushort)plaintext.Length);
            _gcm.Encrypt(nonce, plaintext, packet.AsSpan(21, plaintext.Length), packet.AsSpan(21 + plaintext.Length, 16), aad);
            return packet;
        }
    }

    internal bool TryDecrypt(ReadOnlySpan<byte> packet, out byte[] plaintext, bool allowReplayForHandshakeAcknowledgment = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            plaintext = [];
            if (packet.Length < 37 || BinaryPrimitives.ReadUInt16BigEndian(packet[1..]) != DtlsProtocol.Version ||
                BinaryPrimitives.ReadUInt16BigEndian(packet[3..]) != 1 ||
                BinaryPrimitives.ReadUInt16BigEndian(packet[11..]) != packet.Length - 13) return false;
            var sequence = DtlsProtocol.Read48(packet[5..]);
            var replay = _initialized && sequence <= _highest &&
                (_highest - sequence >= 64 || (_seen & (1UL << (int)(_highest - sequence))) != 0);
            if (replay && !allowReplayForHandshakeAcknowledgment) return false;
            if (_decryptAttempts++ >= MaximumInvocations) throw new AuthenticationException("DTLS receive key exhausted.");
            var size = packet.Length - 37;
            Span<byte> nonce = stackalloc byte[12];
            _iv.CopyTo(nonce); packet.Slice(13, 8).CopyTo(nonce[4..]);
            Span<byte> aad = stackalloc byte[13];
            packet.Slice(3, 8).CopyTo(aad); aad[8] = packet[0]; packet.Slice(1, 2).CopyTo(aad[9..]);
            BinaryPrimitives.WriteUInt16BigEndian(aad[11..], (ushort)size);
            var result = new byte[size];
            try { _gcm.Decrypt(nonce, packet.Slice(21, size), packet[^16..], result, aad); }
            catch (AuthenticationTagMismatchException) { CryptographicOperations.ZeroMemory(result); return false; }
            if (replay) { plaintext = result; return true; }
            if (!_initialized) { _highest = sequence; _seen = 1; _initialized = true; }
            else if (sequence > _highest)
            { var delta = sequence - _highest; _seen = delta >= 64 ? 1 : (_seen << (int)delta) | 1; _highest = sequence; }
            else _seen |= 1UL << (int)(_highest - sequence);
            plaintext = result;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _gcm.Dispose(); CryptographicOperations.ZeroMemory(_iv);
        }
    }
}
