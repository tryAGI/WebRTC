using System.Buffers.Binary;
using System.Text;
using tryAGI.WebRTC;

internal static class SctpTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("SCTP/DCEP assertion failed"); }
    // Synthetic packet independently checksummed by Go's standard hash/crc32 Castagnoli implementation.
    private static byte[] Vector() => Convert.FromHexString("1388138801020304ef84a2d400030013010203040005000700000033aabbcc00");
    internal static void Framing()
    {
        var bytes = Vector(); Check(SctpPacket.TryParse(bytes, out var packet));
        Check(packet.SourcePort == 5000 && packet.DestinationPort == 5000 && packet.VerificationTag == 0x01020304);
        var chunks = packet.Chunks; Check(chunks.MoveNext()); var chunk = chunks.Current;
        Check(chunk.Type == 0 && chunk.Flags == 3 && chunk.Body.Length == 15 && chunk.Body[^3..].SequenceEqual(new byte[] { 0xaa, 0xbb, 0xcc }));
        Check(!chunks.MoveNext());
        Check(SctpPacket.ComputeChecksum(bytes) == 0xd4a284ef);
        for (var i = 0; i < bytes.Length; i++) { var changed = bytes.ToArray(); changed[i] ^= 1; Check(!SctpPacket.TryParse(changed, out _)); }
        for (var i = 0; i < bytes.Length; i++) Check(!SctpPacket.TryParse(bytes.AsSpan(0, i), out _));
        bytes[^1] = 255; Recheck(bytes); Check(SctpPacket.TryParse(bytes, out _)); // Padding has no semantic content.
    }
    internal static void Bounds()
    {
        var packet = Vector(); packet[14] = 0; packet[15] = 3; Recheck(packet); Check(!SctpPacket.TryParse(packet, out _));
        packet = Vector(); packet[14] = 0xff; packet[15] = 0xff; Recheck(packet); Check(!SctpPacket.TryParse(packet, out _));
        packet = Vector(); packet[0] = packet[1] = 0; Recheck(packet); Check(!SctpPacket.TryParse(packet, out _));
        var many = new byte[12 + 257 * 4]; many[0] = many[2] = 1;
        for (var i = 12; i < many.Length; i += 4) many[i + 3] = 4;
        Recheck(many); Check(!SctpPacket.TryParse(many, out _));
        var empty = default(SctpPacket); Check(empty.SourcePort == 0 && empty.DestinationPort == 0 && empty.VerificationTag == 0);
        Check(!empty.Chunks.MoveNext());
    }
    internal static void Dcep()
    {
        var expected = Convert.FromHexString("0380010000000000000a00046f61692d6576656e74736a736f6e");
        var value = new DataChannelParameters("oai-events", "json", false, DataChannelReliability.Reliable, 0, 256);
        Check(DataChannelProtocol.EncodeOpen(value).AsSpan().SequenceEqual(expected));
        Check(DataChannelProtocol.TryParseOpen(expected, out var parsed) && parsed == value);
        foreach (var reliability in Enum.GetValues<DataChannelReliability>())
            foreach (var ordered in new[] { false, true })
            {
                value = new("Канал", "", ordered, reliability, reliability == DataChannelReliability.Reliable ? 0u : 42u, 512);
                var bytes = DataChannelProtocol.EncodeOpen(value);
                Check(DataChannelProtocol.TryParseOpen(bytes, out parsed) && parsed == value);
            }
        Check(DataChannelProtocol.IsAcknowledgment(new byte[] { 2 }) && DataChannelProtocol.IsAcknowledgment(new byte[] { 2, 0, 0, 0 }));
        Check(!DataChannelProtocol.IsAcknowledgment(new byte[] { 2, 0 }) && !DataChannelProtocol.IsAcknowledgment(new byte[] { 2, 0, 1, 0 }));
        expected[4] = 255; Check(DataChannelProtocol.TryParseOpen(expected, out parsed) && parsed!.ReliabilityParameter == 0);
    }
    internal static void DcepMalformed()
    {
        var bytes = DataChannelProtocol.EncodeOpen(new("label", "protocol", true, DataChannelReliability.Reliable, 0, 256));
        for (var i = 0; i < bytes.Length; i++) Check(!DataChannelProtocol.TryParseOpen(bytes.AsSpan(0, i), out _));
        foreach (var kind in new byte[] { 3, 4, 127, 131, 255 }) { var changed = bytes.ToArray(); changed[1] = kind; Check(!DataChannelProtocol.TryParseOpen(changed, out _)); }
        var invalid = bytes.ToArray(); invalid[12] = 0xff; Check(!DataChannelProtocol.TryParseOpen(invalid, out _));
        invalid = bytes.ToArray(); invalid[8] = 255; Check(!DataChannelProtocol.TryParseOpen(invalid, out _));
        try { DataChannelProtocol.EncodeOpen(new(new string('x', 1025), "", true, DataChannelReliability.Reliable, 0, 256)); Check(false); }
        catch (ArgumentOutOfRangeException) { }
        try { DataChannelProtocol.EncodeOpen(new("\ud800", "", true, DataChannelReliability.Reliable, 0, 256)); Check(false); }
        catch (EncoderFallbackException) { }
        var random = new Random(0x53435450);
        for (var i = 0; i < 2000; i++) { var malformed = new byte[random.Next(0, 2048)]; random.NextBytes(malformed); SctpPacket.TryParse(malformed, out _); DataChannelProtocol.TryParseOpen(malformed, out _); }
    }
    private static void Recheck(byte[] bytes) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), SctpPacket.ComputeChecksum(bytes));
}
