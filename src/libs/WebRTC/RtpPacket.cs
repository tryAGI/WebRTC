using System.Buffers.Binary;

namespace tryAGI.WebRTC;

/// <summary>A validated RTP v2 datagram view. Parsing does not authenticate media.</summary>
public readonly ref struct RtpPacket
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _payloadOffset;
    private readonly int _payloadLength;
    private readonly int _extensionOffset;
    private readonly int _extensionLength;

    private RtpPacket(ReadOnlySpan<byte> data, int payloadOffset, int payloadLength,
        int extensionOffset, int extensionLength)
    {
        _data = data;
        _payloadOffset = payloadOffset;
        _payloadLength = payloadLength;
        _extensionOffset = extensionOffset;
        _extensionLength = extensionLength;
    }

    public bool Marker => !_data.IsEmpty && (_data[1] & 0x80) != 0;
    public byte PayloadType => _data.IsEmpty ? (byte)0 : (byte)(_data[1] & 0x7F);
    public ushort SequenceNumber => _data.IsEmpty ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(_data[2..]);
    public uint Timestamp => _data.IsEmpty ? 0 : BinaryPrimitives.ReadUInt32BigEndian(_data[4..]);
    public uint SynchronizationSource => _data.IsEmpty ? 0 : BinaryPrimitives.ReadUInt32BigEndian(_data[8..]);
    public int ContributingSourceCount => _data.IsEmpty ? 0 : _data[0] & 0x0F;
    public bool HasExtension => _extensionOffset != 0;
    public ushort ExtensionProfile => !HasExtension ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(_data[_extensionOffset..]);
    public ReadOnlySpan<byte> ExtensionData => !HasExtension ? [] : _data.Slice(_extensionOffset + 4, _extensionLength);
    public ReadOnlySpan<byte> Payload => _data.Slice(_payloadOffset, _payloadLength);

    /// <summary>Validates fixed fields, CSRCs, extension length and padding before exposing slices.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out RtpPacket packet)
    {
        packet = default;
        if (data.Length < 12 || (data[0] >> 6) != 2)
        {
            return false;
        }
        var offset = 12 + 4 * (data[0] & 0x0F);
        if (offset > data.Length)
        {
            return false;
        }

        var extensionOffset = 0;
        var extensionLength = 0;
        if ((data[0] & 0x10) != 0)
        {
            if (data.Length - offset < 4)
            {
                return false;
            }
            extensionOffset = offset;
            extensionLength = 4 * BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (extensionLength > data.Length - offset - 4)
            {
                return false;
            }
            offset += 4 + extensionLength;
        }

        var payloadLength = data.Length - offset;
        if ((data[0] & 0x20) != 0)
        {
            if (payloadLength == 0 || data[^1] == 0 || data[^1] > payloadLength)
            {
                return false;
            }
            payloadLength -= data[^1];
        }
        packet = new RtpPacket(data, offset, payloadLength, extensionOffset, extensionLength);
        return true;
    }

    public bool TryGetContributingSource(int index, out uint source)
    {
        source = 0;
        if (index < 0 || index >= ContributingSourceCount)
        {
            return false;
        }
        source = BinaryPrimitives.ReadUInt32BigEndian(_data[(12 + 4 * index)..]);
        return true;
    }
}
