using System.Buffers.Binary;
using tryAGI.WebRTC;

internal static class MediaTests
{
    private static void Check(bool value) { if (!value) throw new IOException("Media framing assertion failed"); }
    private static byte[] Packet(ushort profile, byte[] elements)
    {
        var data = new byte[16 + elements.Length]; data[0] = 0x90; data[1] = 111;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), profile);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), (ushort)(elements.Length / 4)); elements.CopyTo(data, 16); return data;
    }
    internal static void Extensions()
    {
        Check(RtpPacket.TryParse(Packet(0xBEDE, [0, 0x12, 97, 98, 99, 0, 0, 0]), out var one));
        Check(RtpHeaderExtensions.TryRead(one, 1, out var value) && value.SequenceEqual("abc"u8));
        Check(RtpHeaderExtensions.TryValidateAndRead(one, 2, out _, out var present) && !present);
        Check(RtpPacket.TryParse(Packet(0x100F, [0, 200, 3, 97, 98, 99, 0, 0]), out var two));
        Check(RtpHeaderExtensions.TryRead(two, 200, out value) && value.SequenceEqual("abc"u8));
        Check(RtpPacket.TryParse(Packet(0xBEDE, [0x10, 97, 0x10, 98]), out var duplicate));
        Check(!RtpHeaderExtensions.TryValidateAndRead(duplicate, 1, out _, out _));
        Check(RtpPacket.TryParse(Packet(0xBEDE, [0x1F, 97, 0, 0]), out var truncated));
        Check(!RtpHeaderExtensions.TryValidateAndRead(truncated, 1, out _, out _));
        Check(RtpPacket.TryParse(Packet(0x1000, [1, 20, 0, 0]), out var truncatedTwo));
        Check(!RtpHeaderExtensions.TryValidateAndRead(truncatedTwo, 1, out _, out _));
        Check(RtpPacket.TryParse(Packet(0xBEDE, [0xF0, 0x1F, 97, 0]), out var terminal));
        Check(RtpHeaderExtensions.TryValidateAndRead(terminal, 1, out _, out present) && !present);
        Check(RtpPacket.TryParse(Packet(0x2222, [0x1F, 97, 0, 0]), out var unknown));
        Check(RtpHeaderExtensions.TryValidateAndRead(unknown, 1, out _, out present) && !present);
        var random = new Random(5428);
        for (var i = 0; i < 1000; i++)
        {
            var elements = new byte[4 * (1 + i % 16)]; random.NextBytes(elements);
            Check(RtpPacket.TryParse(Packet(i % 2 == 0 ? (ushort)0xBEDE : (ushort)0x1000, elements), out var packet));
            RtpHeaderExtensions.TryValidateAndRead(packet, 1, out _, out _);
        }
    }
    internal static void Control()
    {
        var rr = Convert.FromHexString("80C9000100000003"); Check(RtcpFraming.IsValid(rr));
        var sr = Convert.FromHexString("80C80006000000030000000100000002000000030000000400000005"); Check(RtcpFraming.IsValid(sr));
        Check(RtcpFraming.IsValid(rr.Concat(sr).ToArray()));
        Check(RtcpFraming.IsValid(Convert.FromHexString("81CE00020000000300000004"))); // Reduced-size PLI.
        Check(!RtcpFraming.IsValid(Convert.FromHexString("81C9000100000003"))); // Missing reception report.
        Check(!RtcpFraming.IsValid(Convert.FromHexString("80C9000200000003")));
        Check(!RtcpFraming.IsValid(rr.Concat(new byte[] { 1 }).ToArray()));
        var padded = Convert.FromHexString("A0C900020000000300000004"); Check(RtcpFraming.IsValid(padded));
        Check(!RtcpFraming.IsValid(padded.Concat(rr).ToArray()));
        padded[^1] = 0; Check(!RtcpFraming.IsValid(padded));
        padded[^1] = 9; Check(!RtcpFraming.IsValid(padded));
        for (var n = 0; n < sr.Length; n++) Check(!RtcpFraming.IsValid(sr.AsSpan(0, n)));
        Check(!RtcpFraming.IsValid(Enumerable.Repeat(rr, 33).SelectMany(p => p).ToArray()));
    }
}
