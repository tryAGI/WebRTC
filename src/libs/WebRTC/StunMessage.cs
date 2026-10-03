using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace tryAGI.WebRTC;

/// <summary>A validated STUN datagram view. The caller owns the underlying buffer.</summary>
public readonly ref struct StunMessage
{
    public const int HeaderLength = 20;
    public const uint MagicCookie = 0x2112A442;
    public const ushort BindingRequest = 0x0001;
    public const ushort MessageIntegrity = 0x0008;
    public const ushort XorMappedAddress = 0x0020;
    public const ushort Fingerprint = 0x8028;

    private readonly ReadOnlySpan<byte> _data;

    private StunMessage(ReadOnlySpan<byte> data) => _data = data;

    public ushort Type => _data.IsEmpty ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(_data);
    public ReadOnlySpan<byte> TransactionId => _data.IsEmpty ? [] : _data.Slice(8, 12);
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>Validates exactly one datagram, including all attribute lengths and padding boundaries.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out StunMessage message)
    {
        message = default;
        if (data.Length < HeaderLength || (data[0] & 0xC0) != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != MagicCookie)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if ((length & 3) != 0 || data.Length != HeaderLength + length)
        {
            return false;
        }

        var offset = HeaderLength;
        while (offset < data.Length)
        {
            if (data.Length - offset < 4)
            {
                return false;
            }
            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var paddedLength = (valueLength + 3) & ~3;
            if (paddedLength > data.Length - offset - 4)
            {
                return false;
            }
            offset += 4 + paddedLength;
        }

        message = new StunMessage(data);
        return true;
    }

    public AttributeEnumerator GetAttributes() => new(_data);

    /// <summary>Returns an attribute only when it occurs exactly once.</summary>
    public bool TryGetUniqueAttribute(ushort type, out ReadOnlySpan<byte> value)
    {
        value = default;
        var found = false;
        var attributes = GetAttributes();
        while (attributes.MoveNext())
        {
            if (attributes.Type != type)
            {
                continue;
            }
            if (found)
            {
                value = default;
                return false;
            }
            found = true;
            value = attributes.Value;
        }
        return found;
    }

    /// <summary>
    /// Verifies the ICE-compatible HMAC-SHA1 attribute using the caller's credential key.
    /// This strict envelope permits only a final FINGERPRINT after MESSAGE-INTEGRITY.
    /// </summary>
    public bool VerifyMessageIntegritySha1(ReadOnlySpan<byte> key)
    {
        if (_data.IsEmpty || key.IsEmpty)
        {
            return false;
        }

        var offset = -1;
        ReadOnlySpan<byte> expected = default;
        var attributes = GetAttributes();
        while (attributes.MoveNext())
        {
            if (attributes.Type == MessageIntegrity)
            {
                if (offset >= 0 || attributes.Value.Length != 20)
                {
                    return false;
                }
                offset = attributes.Offset;
                expected = attributes.Value;
            }
            else if (offset >= 0 && (attributes.Type != Fingerprint ||
                     attributes.Value.Length != 4 || attributes.Offset + 8 != _data.Length))
            {
                return false;
            }
        }
        if (offset < 0)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        _data[..HeaderLength].CopyTo(header);
        // The header length covers the integrity attribute, but the HMAC input
        // stops immediately before that attribute (RFC 8489 section 14.5).
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(offset + 24 - HeaderLength));
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key);
        hmac.AppendData(header);
        hmac.AppendData(_data.Slice(HeaderLength, offset - HeaderLength));
        Span<byte> actual = stackalloc byte[20];
        return hmac.TryGetHashAndReset(actual, out var written) && written == 20 &&
               CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Checks a unique final CRC fingerprint. A fingerprint is not authentication.</summary>
    public bool VerifyFingerprint()
    {
        var found = false;
        var attributes = GetAttributes();
        while (attributes.MoveNext())
        {
            if (attributes.Type != Fingerprint)
            {
                continue;
            }
            if (found || attributes.Value.Length != 4 || attributes.Offset + 8 != _data.Length)
            {
                return false;
            }
            found = true;
            var expected = BinaryPrimitives.ReadUInt32BigEndian(attributes.Value);
            if ((ComputeCrc32(_data[..attributes.Offset]) ^ 0x5354554Eu) != expected)
            {
                return false;
            }
        }
        return found;
    }

    /// <summary>Decodes a unique IPv4 or IPv6 XOR-MAPPED-ADDRESS without performing DNS or I/O.</summary>
    public bool TryGetXorMappedEndpoint(out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (!TryGetUniqueAttribute(XorMappedAddress, out var value) || value.Length < 4 || value[0] != 0)
        {
            return false;
        }
        var addressLength = value[1] switch { 1 => 4, 2 => 16, _ => 0 };
        if (addressLength == 0 || value.Length != addressLength + 4)
        {
            return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (MagicCookie >> 16);
        Span<byte> address = stackalloc byte[16];
        // Cookie plus transaction ID is the IPv6 mask; IPv4 uses just the cookie.
        for (var i = 0; i < addressLength; i++)
        {
            address[i] = (byte)(value[4 + i] ^ _data[4 + i]);
        }
        endpoint = new IPEndPoint(new IPAddress(address[..addressLength]), (int)port);
        return true;
    }

    /// <summary>Writes an unauthenticated Binding request header with a caller-supplied 96-bit ID.</summary>
    public static bool TryWriteBindingRequest(Span<byte> destination, ReadOnlySpan<byte> transactionId)
    {
        if (destination.Length < HeaderLength || transactionId.Length != 12)
        {
            return false;
        }
        // Preserve an ID that aliases the destination header before overwriting it.
        Span<byte> id = stackalloc byte[12];
        transactionId.CopyTo(id);
        destination[..HeaderLength].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(destination, BindingRequest);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], MagicCookie);
        id.CopyTo(destination[8..]);
        return true;
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
        }
        return ~crc;
    }

    public ref struct AttributeEnumerator
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _nextOffset;

        internal AttributeEnumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _nextOffset = HeaderLength;
            Offset = 0;
            Type = 0;
            Value = default;
        }

        public int Offset { get; private set; }
        public ushort Type { get; private set; }
        public ReadOnlySpan<byte> Value { get; private set; }

        public bool MoveNext()
        {
            if (_nextOffset >= _data.Length)
            {
                Value = default;
                return false;
            }
            Offset = _nextOffset;
            Type = BinaryPrimitives.ReadUInt16BigEndian(_data[Offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(_data[(Offset + 2)..]);
            Value = _data.Slice(Offset + 4, length);
            _nextOffset += 4 + ((length + 3) & ~3);
            return true;
        }
    }
}
