using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace tryAGI.WebRTC;

/// <summary>Writes one bounded STUN datagram into caller-owned storage.</summary>
public ref struct StunMessageWriter
{
    private readonly Span<byte> _buffer;
    private int _length;
    private bool _finished;

    public StunMessageWriter(Span<byte> buffer, ushort type, scoped ReadOnlySpan<byte> transactionId)
    {
        if (buffer.Length < StunMessage.HeaderLength || (type & 0xC000) != 0 || transactionId.Length != 12)
            throw new ArgumentException("A STUN writer requires a header, a valid type and a 96-bit transaction ID.");
        Span<byte> id = stackalloc byte[12];
        transactionId.CopyTo(id);
        _buffer = buffer;
        _length = StunMessage.HeaderLength;
        _finished = false;
        buffer[.._length].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(buffer, type);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], StunMessage.MagicCookie);
        id.CopyTo(buffer[8..]);
    }

    /// <summary>Appends an attribute; integrity and fingerprint must be written by TryComplete.</summary>
    public bool TryAddAttribute(ushort type, scoped ReadOnlySpan<byte> value)
    {
        if (_finished || type is StunMessage.MessageIntegrity or StunMessage.Fingerprint || value.Length > ushort.MaxValue)
            return false;
        var padded = (value.Length + 3) & ~3;
        if (!HasSpace(4 + padded)) return false;
        // Copy first so even input overlapping this attribute's header is preserved.
        value.CopyTo(_buffer.Slice(_length + 4, value.Length));
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[_length..], type);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[(_length + 2)..], (ushort)value.Length);
        _buffer.Slice(_length + 4 + value.Length, padded - value.Length).Clear();
        _length += 4 + padded;
        return true;
    }

    public bool TryAddUInt32(ushort type, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return TryAddAttribute(type, bytes);
    }

    public bool TryAddUInt64(ushort type, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return TryAddAttribute(type, bytes);
    }

    public bool TryAddXorMappedAddress(IPEndPoint endpoint)
    {
        if (_length < StunMessage.HeaderLength || _finished) return false;
        ArgumentNullException.ThrowIfNull(endpoint);
        Span<byte> value = stackalloc byte[20];
        if (!endpoint.Address.TryWriteBytes(value[4..], out var addressLength) || addressLength is not (4 or 16))
            return false;
        value[0] = 0;
        value[1] = (byte)(addressLength == 4 ? 1 : 2);
        BinaryPrimitives.WriteUInt16BigEndian(value[2..], (ushort)(endpoint.Port ^ 0x2112));
        for (var i = 0; i < addressLength; i++) value[4 + i] ^= _buffer[4 + i];
        return TryAddAttribute(StunMessage.XorMappedAddress, value[..(addressLength + 4)]);
    }

    /// <summary>
    /// Finishes atomically on insufficient capacity. An empty key omits authentication;
    /// ICE requires a nonempty credential key and a fingerprint.
    /// </summary>
    public bool TryComplete(ReadOnlySpan<byte> integrityKey, bool fingerprint, out int written)
    {
        written = 0;
        var extra = (integrityKey.IsEmpty ? 0 : 24) + (fingerprint ? 8 : 0);
        if (_finished || !HasSpace(extra) || integrityKey.Overlaps(_buffer)) return false;
        if (!integrityKey.IsEmpty)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_buffer[2..], (ushort)(_length + 24 - StunMessage.HeaderLength));
            Span<byte> hash = stackalloc byte[20];
            HMACSHA1.HashData(integrityKey, _buffer[.._length], hash);
            BinaryPrimitives.WriteUInt16BigEndian(_buffer[_length..], StunMessage.MessageIntegrity);
            BinaryPrimitives.WriteUInt16BigEndian(_buffer[(_length + 2)..], 20);
            hash.CopyTo(_buffer[(_length + 4)..]);
            _length += 24;
        }
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[2..], (ushort)(_length + (fingerprint ? 8 : 0) - StunMessage.HeaderLength));
        if (fingerprint)
        {
            var crc = StunMessage.ComputeCrc32(_buffer[.._length]) ^ 0x5354554Eu;
            BinaryPrimitives.WriteUInt16BigEndian(_buffer[_length..], StunMessage.Fingerprint);
            BinaryPrimitives.WriteUInt16BigEndian(_buffer[(_length + 2)..], 4);
            BinaryPrimitives.WriteUInt32BigEndian(_buffer[(_length + 4)..], crc);
            _length += 8;
        }
        _finished = true;
        written = _length;
        return true;
    }

    private bool HasSpace(int size) => _length >= StunMessage.HeaderLength && size <= _buffer.Length - _length &&
        _length + size - StunMessage.HeaderLength <= ushort.MaxValue;
}
