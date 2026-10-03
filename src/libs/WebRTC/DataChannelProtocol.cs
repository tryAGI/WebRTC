using System.Buffers.Binary;
using System.Text;

namespace tryAGI.WebRTC;

public enum DataChannelReliability { Reliable, RetransmissionLimited, Timed }
public sealed record DataChannelParameters(string Label, string Protocol, bool Ordered,
    DataChannelReliability Reliability, uint ReliabilityParameter, ushort Priority);

/// <summary>Bounded DCEP encoding and decoding. Payload protocol identifier 50 is required.</summary>
public static class DataChannelProtocol
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] EncodeOpen(DataChannelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Enum.IsDefined(parameters.Reliability)) throw new ArgumentOutOfRangeException(nameof(parameters));
        if (Utf8.GetByteCount(parameters.Label) > 1024 || Utf8.GetByteCount(parameters.Protocol) > 1024) throw new ArgumentOutOfRangeException(nameof(parameters));
        var label = Utf8.GetBytes(parameters.Label); var protocol = Utf8.GetBytes(parameters.Protocol);
        var result = new byte[12 + label.Length + protocol.Length]; result[0] = 3;
        result[1] = (byte)((parameters.Ordered ? 0 : 128) | (int)parameters.Reliability);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), parameters.Priority);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), parameters.Reliability == DataChannelReliability.Reliable ? 0 : parameters.ReliabilityParameter);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), (ushort)label.Length);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), (ushort)protocol.Length);
        label.CopyTo(result, 12); protocol.CopyTo(result, 12 + label.Length); return result;
    }
    public static bool TryParseOpen(ReadOnlySpan<byte> bytes, out DataChannelParameters? parameters)
    {
        parameters = null;
        if (bytes.Length < 12 || bytes[0] != 3 || bytes[1] is not (0 or 1 or 2 or 128 or 129 or 130)) return false;
        var label = BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]); var protocol = BinaryPrimitives.ReadUInt16BigEndian(bytes[10..]);
        if (label > 1024 || protocol > 1024 || bytes.Length != 12 + label + protocol) return false;
        try
        {
            var reliability = (DataChannelReliability)(bytes[1] & 127);
            parameters = new(Utf8.GetString(bytes.Slice(12, label)), Utf8.GetString(bytes.Slice(12 + label, protocol)),
                (bytes[1] & 128) == 0, reliability, reliability == DataChannelReliability.Reliable ? 0 : BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]),
                BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]));
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
    // Send canonical RFC 8832 ACKs. Accept the exactly four-byte zero-padded
    // form emitted by the pinned Pion datachannel peer as a bounded compatibility exception.
    public static bool IsAcknowledgment(ReadOnlySpan<byte> bytes) =>
        bytes.SequenceEqual(new byte[] { 2 }) || bytes.SequenceEqual(new byte[] { 2, 0, 0, 0 });
}
