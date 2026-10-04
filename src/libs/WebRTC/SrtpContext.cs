using System.Buffers.Binary;
using System.Security.Cryptography;

namespace tryAGI.WebRTC;

/// <summary>DTLS-SRTP protection profile identifiers.</summary>
public enum SrtpProfile : ushort
{
    Aes128CmHmacSha1_80 = 0x0001,
    AeadAes128Gcm = 0x0007,
    AeadAes256Gcm = 0x0008,
}

public enum SrtpDirection { Send, Receive }

/// <summary>
/// One directional SRTP/SRTCP key generation. Does not negotiate or authenticate keys.
/// Use fresh DTLS-derived keys for each direction and generation, never recreate a sender
/// with the same key/salt or reuse its SSRC/sequence space. Methods are serialized internally.
/// </summary>
public sealed class SrtpContext : IDisposable
{
    public const int MaximumPacketLength = 4096;
    public const int MaximumSources = 32;
    private const ulong MaximumRtpIndex = (1UL << 48) - 1;
    private readonly object _gate = new();
    private readonly SrtpDirection _direction;
    private readonly Cipher _rtp;
    private readonly Cipher _rtcp;
    private readonly Dictionary<uint, ReplayState> _rtpSources = [];
    private readonly Dictionary<uint, ReplayState> _rtcpSources = [];
    private ulong _rtpInvocations;
    private ulong _rtcpInvocations;
    private bool _disposed;

    public SrtpProfile Profile { get; }
    public int RtpOverhead => Profile == SrtpProfile.Aes128CmHmacSha1_80 ? 10 : 16;
    public int RtcpOverhead => RtpOverhead + 4;

    public SrtpContext(SrtpProfile profile, SrtpDirection direction,
        ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        var keyLength = profile == SrtpProfile.AeadAes256Gcm ? 32 : 16;
        var saltLength = profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12;
        if (masterKey.Length != keyLength) throw new ArgumentException("Invalid master key length.", nameof(masterKey));
        if (masterSalt.Length != saltLength) throw new ArgumentException("Invalid master salt length.", nameof(masterSalt));
        Profile = profile;
        _direction = direction;
        _rtp = new Cipher(profile, masterKey, masterSalt, 0);
        try { _rtcp = new Cipher(profile, masterKey, masterSalt, 3); }
        catch { _rtp.Dispose(); throw; }
    }

    /// <summary>Encrypts a valid RTP packet. Requires a separate destination buffer.
    /// Outbound indices must increase strictly: use RTX or retransmit the original ciphertext.</summary>
    public bool TryProtectRtp(ReadOnlySpan<byte> packet, Span<byte> destination, out int written)
    {
        lock (_gate)
        {
            RequireDirection(SrtpDirection.Send);
            written = 0;
            if (!ValidBuffers(packet, destination, RtpOverhead) || !RtpPacket.TryParse(packet, out _) ||
                !TryRtpHeader(packet, out var headerLength, out var source, out var sequence) ||
                !TryState(_rtpSources, source, out var state) || !TryIndex(state, sequence, out var index) ||
                (state.Initialized && index <= state.Highest) || _rtpInvocations >= 1UL << 48) return false;

            // Reserve the nonce before calling crypto, including exceptional failures.
            state.Accept(index);
            _rtpSources[source] = state;
            _rtpInvocations++;
            packet[..headerLength].CopyTo(destination);
            _rtp.Encrypt(packet[headerLength..], destination.Slice(headerLength, packet.Length - headerLength),
                destination.Slice(packet.Length, RtpOverhead), packet[..headerLength], source, index, false);
            if (Profile == SrtpProfile.Aes128CmHmacSha1_80)
                _rtp.Authenticate(destination[..packet.Length], (uint)(index >> 16), destination.Slice(packet.Length, RtpOverhead));
            written = packet.Length + RtpOverhead;
            return true;
        }
    }

    /// <summary>Authenticates, checks replay and decrypts. False leaves destination and state untouched.</summary>
    public bool TryUnprotectRtp(ReadOnlySpan<byte> packet, Span<byte> destination, out int written) => TryUnprotectRtp(packet, destination, out written, out _);
    internal bool TryUnprotectRtp(ReadOnlySpan<byte> packet, Span<byte> destination, out int written, out PacketReason reason)
    {
        lock (_gate)
        {
            RequireDirection(SrtpDirection.Receive);
            written = 0; reason = PacketReason.InvalidFraming;
            var length = packet.Length - RtpOverhead;
            if (length < 12 || length > MaximumPacketLength || destination.Length < length || packet.Overlaps(destination) ||
                !TryRtpHeader(packet[..length], out var headerLength, out var source, out var sequence) ||
                !TryState(_rtpSources, source, out var state) || !TryIndex(state, sequence, out var index) || _rtpInvocations >= 1UL << 48) return false;
            // This is a replay-window decision, not evidence of authenticated duplicate identity.
            if (!state.CanAccept(index)) { reason = PacketReason.ReplayOrTooOld; return false; }
            reason = PacketReason.Authentication;
            Span<byte> plaintext = stackalloc byte[length];
            try
            {
                if (Profile == SrtpProfile.Aes128CmHmacSha1_80 &&
                    !_rtp.Verify(packet[..length], (uint)(index >> 16), packet[length..])) return false;
                packet[..headerLength].CopyTo(plaintext);
                if (!_rtp.Decrypt(packet.Slice(headerLength, length - headerLength), plaintext[headerLength..],
                    packet[length..], packet[..headerLength], source, index, false)) return false;
                if (!RtpPacket.TryParse(plaintext, out _)) { reason = PacketReason.InvalidFraming; return false; }
                state.Accept(index);
                _rtpSources[source] = state;
                _rtpInvocations++;
                plaintext.CopyTo(destination);
                written = length; reason = PacketReason.None;
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    /// <summary>Encrypts a structurally valid RTCP datagram, including compound/reduced-size RTCP.</summary>
    public bool TryProtectRtcp(ReadOnlySpan<byte> packet, Span<byte> destination, out int written)
    {
        lock (_gate)
        {
            RequireDirection(SrtpDirection.Send);
            written = 0;
            if (!ValidBuffers(packet, destination, RtcpOverhead) || !ValidRtcp(packet)) return false;
            var source = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
            if (!TryState(_rtcpSources, source, out var state) ||
                (state.Initialized && state.Highest >= int.MaxValue) || _rtcpInvocations >= 1UL << 31) return false;
            var index = state.Initialized ? state.Highest + 1 : 1UL;
            state.Accept(index);
            _rtcpSources[source] = state;
            _rtcpInvocations++;
            var aead = Profile != SrtpProfile.Aes128CmHmacSha1_80;
            var indexOffset = packet.Length + (aead ? RtpOverhead : 0);
            var tagOffset = packet.Length + (aead ? 0 : 4);
            Span<byte> aad = stackalloc byte[12];
            packet[..8].CopyTo(aad);
            BinaryPrimitives.WriteUInt32BigEndian(aad[8..], (uint)index | 0x80000000);
            packet[..8].CopyTo(destination);
            aad[8..].CopyTo(destination[indexOffset..]);
            _rtcp.Encrypt(packet[8..], destination.Slice(8, packet.Length - 8), destination.Slice(tagOffset, RtpOverhead),
                aad, source, index, true);
            if (!aead) _rtcp.Authenticate(destination[..(packet.Length + 4)], null, destination.Slice(tagOffset, RtpOverhead));
            written = packet.Length + RtcpOverhead;
            return true;
        }
    }

    /// <summary>Verifies SRTCP authentication/replay before releasing any plaintext.
    /// Accepts authenticated E=0 control packets; sending always encrypts.</summary>
    public bool TryUnprotectRtcp(ReadOnlySpan<byte> packet, Span<byte> destination, out int written) => TryUnprotectRtcp(packet, destination, out written, out _);
    internal bool TryUnprotectRtcp(ReadOnlySpan<byte> packet, Span<byte> destination, out int written, out PacketReason reason)
    {
        lock (_gate)
        {
            RequireDirection(SrtpDirection.Receive);
            written = 0; reason = PacketReason.InvalidFraming;
            var length = packet.Length - RtcpOverhead;
            if (length < 8 || length > MaximumPacketLength || destination.Length < length || packet.Overlaps(destination) ||
                (packet[0] >> 6) != 2) return false;
            var aead = Profile != SrtpProfile.Aes128CmHmacSha1_80;
            var indexOffset = length + (aead ? RtpOverhead : 0);
            var tagOffset = length + (aead ? 0 : 4);
            var indexWord = BinaryPrimitives.ReadUInt32BigEndian(packet[indexOffset..]);
            var index = indexWord & 0x7FFFFFFF;
            var encrypted = (indexWord & 0x80000000) != 0;
            var source = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
            if (!TryState(_rtcpSources, source, out var state) || _rtcpInvocations >= 1UL << 31) return false;
            if (!state.CanAccept(index)) { reason = PacketReason.ReplayOrTooOld; return false; }
            reason = PacketReason.Authentication;
            Span<byte> plaintext = stackalloc byte[length];
            Span<byte> aad = stackalloc byte[length + 4];
            try
            {
                if (!aead && !_rtcp.Verify(packet[..(length + 4)], null, packet.Slice(tagOffset, RtpOverhead))) return false;
                packet[..(encrypted ? 8 : length)].CopyTo(aad);
                packet.Slice(indexOffset, 4).CopyTo(aad[(encrypted ? 8 : length)..]);
                packet[..8].CopyTo(plaintext);
                if (encrypted)
                {
                    if (!_rtcp.Decrypt(packet.Slice(8, length - 8), plaintext[8..], packet.Slice(tagOffset, RtpOverhead),
                        aad[..12], source, index, true)) return false;
                }
                else
                {
                    if (aead && !_rtcp.Decrypt([], [], packet.Slice(tagOffset, RtpOverhead), aad[..(length + 4)], source, index, true)) return false;
                    packet[..length].CopyTo(plaintext);
                }
                if (!ValidRtcp(plaintext)) return false;
                state.Accept(index);
                _rtcpSources[source] = state;
                _rtcpInvocations++;
                plaintext.CopyTo(destination);
                written = length; reason = PacketReason.None;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(aad);
            }
        }
    }

    private void RequireDirection(SrtpDirection direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_direction != direction) throw new InvalidOperationException("SRTP key contexts are directional.");
    }

    private static bool ValidBuffers(ReadOnlySpan<byte> packet, Span<byte> destination, int overhead) =>
        packet.Length <= MaximumPacketLength && destination.Length >= packet.Length + overhead && !packet.Overlaps(destination);

    private static bool TryState(Dictionary<uint, ReplayState> states, uint source, out ReplayState state) =>
        states.TryGetValue(source, out state) || states.Count < MaximumSources;

    private static bool TryRtpHeader(ReadOnlySpan<byte> packet, out int length, out uint source, out ushort sequence)
    {
        length = 0; source = 0; sequence = 0;
        if (packet.Length < 12 || (packet[0] >> 6) != 2) return false;
        var offset = 12 + 4 * (packet[0] & 15);
        if (offset > packet.Length) return false;
        if ((packet[0] & 0x10) != 0)
        {
            if (offset + 4 > packet.Length) return false;
            offset += 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            if (offset > packet.Length) return false;
        }
        length = offset;
        source = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        return true;
    }

    private static bool ValidRtcp(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8) return false;
        var offset = 0;
        while (offset < packet.Length)
        {
            var remaining = packet[offset..];
            if (remaining.Length < 4 || (remaining[0] >> 6) != 2 || remaining[1] is < 192 or > 223) return false;
            var size = 4 * (1 + BinaryPrimitives.ReadUInt16BigEndian(remaining[2..]));
            if (size > remaining.Length || (offset == 0 && size < 8)) return false;
            if ((remaining[0] & 0x20) != 0 &&
                (size != remaining.Length || remaining[size - 1] == 0 || remaining[size - 1] > size - 4 || remaining[size - 1] % 4 != 0)) return false;
            offset += size;
        }
        return true;
    }

    private static bool TryIndex(ReplayState state, ushort sequence, out ulong index)
    {
        index = sequence;
        if (!state.Initialized) return true;
        var previous = (ushort)state.Highest;
        var roc = (long)(state.Highest >> 16);
        if (previous < 32768 && sequence - previous > 32768) roc--;
        else if (previous >= 32768 && previous - sequence > 32768) roc++;
        if (roc < 0 || roc > uint.MaxValue) return false;
        index = ((ulong)roc << 16) | sequence;
        return index <= MaximumRtpIndex;
    }

    private struct ReplayState
    {
        public bool Initialized;
        public ulong Highest;
        private ulong _seen;
        public readonly bool CanAccept(ulong index) => !Initialized || index > Highest ||
            (Highest - index < 64 && (_seen & (1UL << (int)(Highest - index))) == 0);
        public void Accept(ulong index)
        {
            if (!Initialized) { Initialized = true; Highest = index; _seen = 1; }
            else if (index > Highest)
            {
                var delta = index - Highest;
                _seen = delta >= 64 ? 1 : (_seen << (int)delta) | 1;
                Highest = index;
            }
            else _seen |= 1UL << (int)(Highest - index);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _rtp.Dispose(); _rtcp.Dispose();
            _rtpSources.Clear(); _rtcpSources.Clear();
        }
    }

    private sealed class Cipher : IDisposable
    {
        private readonly Aes? _aes;
        private readonly AesGcm? _gcm;
        private readonly IncrementalHash? _hmac;
        private readonly byte[] _salt = [];

        public Cipher(SrtpProfile profile, ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt, byte label)
        {
            var cm = profile == SrtpProfile.Aes128CmHmacSha1_80;
            var key = Derive(masterKey, masterSalt, label, masterKey.Length);
            try
            {
                _salt = Derive(masterKey, masterSalt, (byte)(label + 2), cm ? 14 : 12);
                if (cm)
                {
                    _aes = Aes.Create();
                    _aes.Key = key;
                    var authenticationKey = Derive(masterKey, masterSalt, (byte)(label + 1), 20);
                    try { _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, authenticationKey); }
                    finally { CryptographicOperations.ZeroMemory(authenticationKey); }
                }
                else _gcm = new AesGcm(key, 16);
            }
            catch { Dispose(); throw; }
            finally { CryptographicOperations.ZeroMemory(key); }
        }

        private static byte[] Derive(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, byte label, int length)
        {
            // DTLS-SRTP uses key_derivation_rate=0. Label occupies byte 7 of
            // the zero-padded 128-bit PRF input; AES-256 uses the same layout.
            using var aes = Aes.Create();
            var keyCopy = key.ToArray();
            try { aes.Key = keyCopy; }
            finally { CryptographicOperations.ZeroMemory(keyCopy); }
            Span<byte> counter = stackalloc byte[16]; counter.Clear();
            Span<byte> block = stackalloc byte[16];
            salt.CopyTo(counter); counter[7] ^= label;
            var result = new byte[length];
            try
            {
                for (var offset = 0; offset < length; offset += 16)
                {
                    aes.EncryptEcb(counter, block, PaddingMode.None);
                    block[..Math.Min(16, length - offset)].CopyTo(result.AsSpan(offset));
                    counter[15]++;
                }
                return result;
            }
            catch { CryptographicOperations.ZeroMemory(result); throw; }
            finally { CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(block); }
        }

        private void Nonce(Span<byte> nonce, uint source, ulong index, bool rtcp)
        {
            nonce.Clear();
            if (_gcm != null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(nonce[2..], source);
                if (rtcp) BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], (uint)index);
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(nonce[6..], (uint)(index >> 16));
                    BinaryPrimitives.WriteUInt16BigEndian(nonce[10..], (ushort)index);
                }
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(nonce[4..], source);
                BinaryPrimitives.WriteUInt64BigEndian(nonce[8..], index << 16);
            }
            for (var i = 0; i < _salt.Length; i++) nonce[i] ^= _salt[i];
        }

        public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output, Span<byte> tag,
            ReadOnlySpan<byte> aad, uint source, ulong index, bool rtcp)
        {
            Span<byte> nonce = stackalloc byte[_gcm == null ? 16 : 12];
            Nonce(nonce, source, index, rtcp);
            if (_gcm != null) _gcm.Encrypt(nonce, input, output, tag, aad);
            else CounterTransform(input, output, nonce);
        }

        public bool Decrypt(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> tag,
            ReadOnlySpan<byte> aad, uint source, ulong index, bool rtcp)
        {
            Span<byte> nonce = stackalloc byte[_gcm == null ? 16 : 12];
            Nonce(nonce, source, index, rtcp);
            if (_gcm == null) { CounterTransform(input, output, nonce); return true; }
            try { _gcm.Decrypt(nonce, input, tag, output, aad); return true; }
            catch (AuthenticationTagMismatchException) { return false; }
        }

        private void CounterTransform(ReadOnlySpan<byte> input, Span<byte> output, Span<byte> counter)
        {
            Span<byte> block = stackalloc byte[16];
            try
            {
                for (var offset = 0; offset < input.Length; offset += 16)
                {
                    _aes!.EncryptEcb(counter, block, PaddingMode.None);
                    var length = Math.Min(16, input.Length - offset);
                    for (var i = 0; i < length; i++) output[offset + i] = (byte)(input[offset + i] ^ block[i]);
                    var next = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(counter[14..]) + 1);
                    BinaryPrimitives.WriteUInt16BigEndian(counter[14..], next);
                }
            }
            finally { CryptographicOperations.ZeroMemory(block); }
        }

        public void Authenticate(ReadOnlySpan<byte> packet, uint? roc, Span<byte> tag)
        {
            _hmac!.AppendData(packet);
            if (roc.HasValue)
            {
                Span<byte> rollover = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(rollover, roc.Value);
                _hmac.AppendData(rollover);
            }
            Span<byte> hash = stackalloc byte[20];
            try { _hmac.GetHashAndReset(hash); hash[..tag.Length].CopyTo(tag); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }

        public bool Verify(ReadOnlySpan<byte> packet, uint? roc, ReadOnlySpan<byte> tag)
        {
            Span<byte> expected = stackalloc byte[10];
            Authenticate(packet, roc, expected);
            try { return CryptographicOperations.FixedTimeEquals(expected, tag); }
            finally { CryptographicOperations.ZeroMemory(expected); }
        }

        public void Dispose()
        {
            _aes?.Dispose(); _gcm?.Dispose(); _hmac?.Dispose();
            CryptographicOperations.ZeroMemory(_salt);
        }
    }
}
