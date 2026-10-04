using System.Buffers.Binary;
using tryAGI.WebRTC;

internal static class RtcpTests
{
    internal static void Check(bool value, string message = "RTCP assertion failed")
    { if (!value) throw new IOException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new IOException($"Expected {typeof(T).Name}"); }
    // Independently assembled from synthetic fields using Python struct.pack and signed-24 masking.
    // No upstream/RFC fixture, example or implementation is copied.
    internal static byte[] Reference() => Convert.FromHexString(
        "82c8001221324354123456789abcdef0876543210000000400000005" +
        "000000634000000700010001000000801122334400010000" +
        "0000006400fffffefffffffeffffffff0000000000000000" +
        "80c9000100000003" +
        "82ca000821324354010d706565722fd098d0b2d0b0d0bd000000000301056f7468657200" +
        "81ce00020000000300000063");
    internal static RtcpPacket[] Model() =>
    [
        new RtcpSenderReport(0x21324354, 0x123456789abcdef0, 0x87654321, 4, 5,
            new RtcpReceptionReport[] { new(99, 64, 7, 65537, 128, 0x11223344, 65536), new(100, 0, -2, 0xfffffffe, uint.MaxValue, 0, 0) }),
        new RtcpReceiverReport(3, []),
        new RtcpSourceDescription(new RtcpSdesChunk[] { new(0x21324354, "peer/Иван"), new(3, "other") }),
        new RtcpPictureLossIndication(3, 99),
    ];
    internal static void Vectors()
    {
        var reference = Reference();
        Check(RtcpPackets.Encode(Model()).SequenceEqual(reference), "Independent struct vector mismatch");
        Check(RtcpPackets.TryParse(reference, out var parsed, out var compound) && compound && parsed.Count == 4);
        var sr = (RtcpSenderReport)parsed[0];
        Check(sr.SenderSource == 0x21324354 && sr.NtpTimestamp == 0x123456789abcdef0 && sr.RtpTimestamp == 0x87654321 && sr.PacketCount == 4 && sr.OctetCount == 5);
        Check(sr.Reports.SequenceEqual(((RtcpSenderReport)Model()[0]).Reports));
        Check(parsed[1] is RtcpReceiverReport { SenderSource: 3, Reports.Count: 0 });
        Check(((RtcpSourceDescription)parsed[2]).Chunks.SequenceEqual(((RtcpSourceDescription)Model()[2]).Chunks));
        Check(parsed[3] == new RtcpPictureLossIndication(3, 99));
        Check(RtcpPackets.Encode(parsed).SequenceEqual(reference));
        Array.Fill(reference, (byte)0);
        Check(sr.Reports[1].CumulativeLost == -2 && ((RtcpSourceDescription)parsed[2]).Chunks[0].CanonicalName == "peer/Иван");
        foreach (var loss in new[] { -8388608, -1, 0, 8388607 })
        {
            var encoded = RtcpPackets.Encode([new RtcpReceiverReport(0, new[] { new RtcpReceptionReport(1, 255, loss, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue) })]);
            Check(RtcpPackets.TryParse(encoded, out var packets, out compound) && !compound);
            Check(((RtcpReceiverReport)packets[0]).Reports[0].CumulativeLost == loss);
            // Direct byte-level signed field check is independent of decoder behavior.
            Check(encoded[13] == (byte)(loss >> 16) && encoded[14] == (byte)(loss >> 8) && encoded[15] == (byte)loss);
        }
    }
    private static byte[] Hex(string value) => Convert.FromHexString(value);
    internal static void SdesAndBye()
    {
        foreach (var data in new[] {
            Hex("81ca0003000000030101610201620000"), // CNAME + NAME.
            Hex("81ca0003000000030803016162000000"), // Valid PRIV prefix.
            Hex("81ca00020000000300000000"), // No CNAME.
            Hex("81ca00020000000302016100"), // NAME only.
        }) Check(RtcpPackets.TryParse(data, out _, out var compound) && !compound);
        Check(RtcpPackets.TryParse(Hex("82cb00040000000300000004056279652f780000"), out var byePackets, out var byeCompound) && !byeCompound);
        Check(byePackets[0] is RtcpGoodbye { Reason: "bye/x" } goodbye && goodbye.Sources.SequenceEqual(new uint[] { 3, 4 }));
        foreach (var bad in new[] {
            "81ca00020000000301000000", // Empty CNAME.
            "81ca000200000003010161ff", // Missing END.
            "81ca00020000000301016101", // Invalid item framing.
            "81ca0003000000030101610000000001", // Excess nonzero padding/trailing bytes.
            "81ca0003000000030101610101620000", // Duplicate CNAME.
            "81ca0002000000030101ff00", // Invalid UTF8.
            "81ca00020000000301010000", // Embedded NUL CNAME.
            "81ca0003000000030103616263000001", // Nonzero chunk padding.
            "81ca00020000000308000000", // Empty PRIV.
            "81ca0003000000030803036162000000", // PRIV prefix exceeds item.
            "82ca000400000003000000000000000300000000", // Duplicate source.
            "80ca000100000000", // Empty SDES.
            "80cb000100000000", // Empty BYE sources.
            "82cb00020000000300000003", // Duplicate BYE sources.
            "81cb00020000000305610000", // Reason truncation.
            "81cb00020000000301610100", // Nonzero reason padding.
            "81cb00020000000301ff0000", // Invalid reason UTF8.
        }) Check(!RtcpPackets.TryParse(Hex(bad), out var rejected, out var compound) && rejected.Count == 0 && !compound, bad);
        var name = RtcpPackets.Encode([new RtcpSourceDescription(new[] { new RtcpSdesChunk(3, "a") })]);
        var other = RtcpPackets.Encode([new RtcpSourceDescription(new[] { new RtcpSdesChunk(3, "b") })]);
        Check(!RtcpPackets.TryParse([.. name, .. other], out _, out _));
        Check(RtcpPackets.TryParse([.. name, .. name], out _, out _));
    }
    internal static void CompoundAndUnknown()
    {
        var rr = Hex("80c9000100000003"); var other = Hex("80c9000100000004");
        var name = RtcpPackets.Encode([new RtcpSourceDescription(new[] { new RtcpSdesChunk(3, "x") })]);
        var both = RtcpPackets.Encode([new RtcpSourceDescription(new RtcpSdesChunk[] { new(3, "x"), new(4, "y") })]);
        Check(RtcpPackets.TryParse([.. rr, .. name], out _, out var compound) && compound);
        Check(RtcpPackets.TryParse([.. rr, .. other, .. name], out _, out compound) && !compound);
        Check(RtcpPackets.TryParse([.. rr, .. other, .. both], out _, out compound) && compound);
        Check(RtcpPackets.TryParse([.. name, .. rr], out _, out compound) && !compound);
        var padded = Hex("a0c900020000000300000004");
        Check(RtcpPackets.TryParse(padded, out var packets, out compound) && !compound && packets[0] is RtcpReceiverReport);
        var unknown = Hex("82ce0003000000030000000400000001"); // Unsupported FMT remains opaque.
        Check(RtcpPackets.TryParse(unknown, out packets, out compound) && !compound && packets[0] is RtcpOpaquePacket { Type: 206, Count: 2 });
        var opaque = (RtcpOpaquePacket)packets[0]; Array.Fill(unknown, (byte)0); Check(opaque.Body.Span[3] == 3);
        Throws<ArgumentException>(() => RtcpPackets.Sender(opaque));
        Throws<NotSupportedException>(() => RtcpPackets.Encode(packets));
        Check(!RtcpPackets.TryParse(Hex("81ce0003000000030000000400000000"), out _, out _)); // PLI has no FCI.
    }
    internal static void BoundsAndCorpus()
    {
        var reference = Reference();
        // A truncation at an exact packet boundary is a valid partial datagram; all other cuts reject.
        var boundaries = new[] { 76, 84, 120, 132 };
        for (var n = 0; n < reference.Length; n++)
            Check(RtcpPackets.TryParse(reference.AsSpan(0, n), out _, out _) == boundaries.Contains(n), $"Truncation {n}");
        Check(!RtcpPackets.TryParse([.. reference, 0], out _, out _));
        foreach (var length in new[] { 1201, 65536 }) Check(!RtcpPackets.TryParse(new byte[length], out _, out _));
        var report = new RtcpReceiverReport(3, []);
        Check(RtcpPackets.TryParse(RtcpPackets.Encode(Enumerable.Repeat<RtcpPacket>(report, 32)), out var packets, out _) && packets.Count == 32);
        Throws<ArgumentOutOfRangeException>(() => RtcpPackets.Encode(Enumerable.Repeat<RtcpPacket>(report, 33)));
        Throws<ArgumentOutOfRangeException>(() => RtcpPackets.Encode([]));
        Throws<ArgumentOutOfRangeException>(() => RtcpPackets.Encode([null!]));
        Throws<ArgumentOutOfRangeException>(() => RtcpPackets.Encode([new RtcpReceiverReport(3, Enumerable.Repeat(new RtcpReceptionReport(4, 0, 0, 0, 0, 0, 0), 32).ToArray())]));
        foreach (var lost in new[] { -8388609, 8388608 })
            Throws<ArgumentOutOfRangeException>(() => RtcpPackets.Encode([new RtcpReceiverReport(3, new[] { new RtcpReceptionReport(4, 0, lost, 0, 0, 0, 0) })]));
        foreach (var name in new[] { "", "\0", new string('x', 256), new string('я', 128), "\ud800" })
            Throws<ArgumentException>(() => RtcpPackets.Encode([new RtcpSourceDescription(new[] { new RtcpSdesChunk(3, name) })]));
        Throws<ArgumentException>(() => RtcpPackets.Encode([new RtcpSourceDescription(new RtcpSdesChunk[] { new(3, "a"), new(3, "a") })]));
        Throws<ArgumentException>(() => RtcpPackets.Encode([new RtcpSourceDescription([])]));
        Throws<ArgumentOutOfRangeException>(() => RtcpPackets.Encode([new RtcpSourceDescription(Enumerable.Range(0, 6).Select(i => new RtcpSdesChunk((uint)i, new string('x', 255))).ToArray())]));
        var random = new Random(4585);
        for (var i = 0; i < 12000; i++)
        {
            var data = new byte[random.Next(0, 1300)]; random.NextBytes(data);
            if (i % 2 == 0 && data.Length >= 8) { data[0] = (byte)(0x80 | i % 32); data[1] = (byte)(200 + i % 8); BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)(data.Length / 4 - 1)); }
            RtcpPackets.TryParse(data, out _, out _);
        }
    }
    internal static void Reception()
    {
        var tracker = new RtpReceptionTracker(99, 48000);
        Check(tracker.CreateReport() == null && !tracker.IsReady);
        Check(!tracker.Observe(65535, uint.MaxValue - 959, TimeSpan.Zero));
        Check(tracker.Observe(0, 0, TimeSpan.FromMilliseconds(20)));
        Check(tracker.CreateReport() is { HighestSequence: 65536, CumulativeLost: 0, FractionLost: 0, Jitter: 0 });
        Check(tracker.Observe(2, 1920, TimeSpan.FromMilliseconds(60)));
        Check(tracker.CreateReport(0x11223344, TimeSpan.FromMilliseconds(1250)) is
            { HighestSequence: 65538, CumulativeLost: 1, FractionLost: 128, Jitter: 0, LastSenderReport: 0x11223344, DelaySinceLastSenderReport: 81920 });
        Check(tracker.Observe(1, 960, TimeSpan.FromMilliseconds(80)));
        Check(tracker.CreateReport() is { CumulativeLost: 0, FractionLost: 0, Jitter: 120 });
        Check(tracker.Observe(1, 960, TimeSpan.FromMilliseconds(100)));
        Check(tracker.CreateReport(0, TimeSpan.FromSeconds(1)) is { CumulativeLost: -1, FractionLost: 0, Jitter: 172, DelaySinceLastSenderReport: 0 });
        Check(!tracker.Observe(30000, 500000, TimeSpan.FromSeconds(1)));
        Check(tracker.CreateReport() is { HighestSequence: 65538, CumulativeLost: -1 });
        Throws<ArgumentOutOfRangeException>(() => tracker.Observe(30001, 500960, TimeSpan.FromMilliseconds(900)));
        Check(tracker.Observe(30001, 500960, TimeSpan.FromMilliseconds(1020)));
        Check(tracker.CreateReport() is { HighestSequence: 30001, CumulativeLost: 0, Jitter: 0 });
        Check(tracker.Observe(33000, 1000000, TimeSpan.FromMilliseconds(1040))); // Last accepted forward delta.
        Check(tracker.CreateReport() is { CumulativeLost: 2998, FractionLost: 255 });
        Check(!tracker.Observe(36000, 1000960, TimeSpan.FromMilliseconds(1060))); // First restart candidate delta.
        Throws<ArgumentOutOfRangeException>(() => tracker.CreateReport(1, TimeSpan.FromTicks(-1)));
        Throws<ArgumentOutOfRangeException>(() => tracker.Observe(36001, 1000960, TimeSpan.FromTicks(-1)));
        foreach (var clock in new[] { 0, 192001 }) Throws<ArgumentOutOfRangeException>(() => _ = new RtpReceptionTracker(1, clock));
        var probation = new RtpReceptionTracker(1, 90000);
        Check(!probation.Observe(10, 0, TimeSpan.Zero)); Check(!probation.Observe(12, 1800, TimeSpan.FromMilliseconds(20)));
        Check(probation.Observe(13, 3600, TimeSpan.FromMilliseconds(40)));
        Check(probation.CreateReport(1, TimeSpan.MaxValue) is { HighestSequence: 13, CumulativeLost: 0, DelaySinceLastSenderReport: uint.MaxValue });
    }
    internal static void Clock()
    {
        var epoch = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Check(RtcpClock.ToNtpTimestamp(epoch) == 0);
        Check(RtcpClock.ToNtpTimestamp(epoch.AddSeconds(1.5)) == 0x180000000);
        Check(RtcpClock.ToNtpTimestamp(epoch.AddTicks(1)) == 429);
        Check(RtcpClock.ToNtpTimestamp(epoch.AddSeconds(uint.MaxValue).AddMilliseconds(500)) == 0xffffffff80000000);
        Check(RtcpClock.ToNtpTimestamp(epoch.AddSeconds(4294967296)) == 0);
        Check(RtcpClock.ToNtpTimestamp(epoch.AddSeconds(4294967297)) == 0x100000000);
        Check(RtcpClock.ToNtpTimestamp(epoch.ToOffset(TimeSpan.FromHours(4))) == 0);
        Check(RtcpClock.Compact(0x123456789abcdef0) == 0x56789abc);
        Throws<ArgumentOutOfRangeException>(() => RtcpClock.ToNtpTimestamp(epoch.AddTicks(-1)));
    }
    private sealed class ClockSource : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        internal void Advance(TimeSpan delay) => Ticks += delay.Ticks;
    }
    private static void Near(TimeSpan actual, double expected)
        => Check(Math.Abs(actual.TotalSeconds - expected) < .000001, $"Interval {actual.TotalSeconds} != {expected}");
    internal static void Schedule()
    {
        var clock = new ClockSource(); var scheduler = new RtcpTransmissionSchedule(100, 200, true, clock, () => .5);
        var regular = 2 / 1.21828; Near(scheduler.Delay(false, out var early), regular); Check(!early);
        Check(scheduler.Delay(true, out early) == TimeSpan.Zero && early);
        scheduler.Sent(200, early); Near(scheduler.Delay(true, out early), 2 * regular); Check(!early);
        Throws<InvalidOperationException>(() => scheduler.Sent(1500, true));
        Throws<InvalidOperationException>(() => scheduler.Sent(1500, false));
        Near(scheduler.Delay(false, out _), 2 * regular); // Failed sends did not mutate size or slot.
        clock.Advance(scheduler.Delay(false, out _) + TimeSpan.FromTicks(1)); scheduler.Sent(200, false);
        Near(scheduler.Delay(false, out _), regular);
        Check(scheduler.Delay(true, out early) == TimeSpan.Zero && early);
        scheduler.DisableEarlyFeedback(); var delay = scheduler.Delay(true, out early); Check(!early && !scheduler.EarlyFeedbackEnabled);
        scheduler.DisableEarlyFeedback(); Check(delay == scheduler.Delay(true, out _));
        Throws<InvalidOperationException>(() => scheduler.Sent(200, true));
        var initial = new RtcpTransmissionSchedule(1000, 100, false, new ClockSource(), () => .5);
        Near(initial.Delay(true, out early), 1 / 1.21828); Check(!early);
        var initialClock = new ClockSource(); var disabled = new RtcpTransmissionSchedule(1000, 100, true, initialClock, () => .5);
        disabled.DisableEarlyFeedback(); var deadline = disabled.Delay(true, out _); initialClock.Advance(TimeSpan.FromMilliseconds(100));
        disabled.DisableEarlyFeedback(); Check(deadline - TimeSpan.FromMilliseconds(100) == disabled.Delay(true, out _));
        var delayedClock = new ClockSource(); var delayed = new RtcpTransmissionSchedule(100, 200, true, delayedClock, () => .5);
        Check(delayed.Delay(true, out early) == TimeSpan.Zero && early);
        delayedClock.Advance(delayed.Delay(false, out _) + TimeSpan.FromTicks(1)); delayed.Sent(200, early);
        Near(delayed.Delay(false, out _), regular); Check(delayed.Delay(true, out early) == TimeSpan.Zero && early);
        delayed.ObserveSize(600); delayedClock.Advance(delayed.Delay(false, out _) + TimeSpan.FromTicks(1)); delayed.Sent(200, false);
        Near(delayed.Delay(false, out _), 223.4375 / 100 / 1.21828); // two EWMA updates.
        foreach (var sample in new[] { 0d, 1d })
        { var s = new RtcpTransmissionSchedule(100, 200, true, new ClockSource(), () => sample); Near(s.Delay(false, out _), regular * (.5 + sample)); }
        foreach (var bytes in new[] { 0, 1501 }) { Throws<ArgumentOutOfRangeException>(() => delayed.ObserveSize(bytes)); Throws<ArgumentOutOfRangeException>(() => delayed.Sent(bytes, false)); }
        foreach (var rate in new[] { 0d, double.NaN, double.PositiveInfinity, 32769d }) Throws<ArgumentOutOfRangeException>(() => _ = new RtcpTransmissionSchedule(rate, 100, true));
        foreach (var sample in new[] { -.01, 1.01, double.NaN }) Throws<InvalidOperationException>(() => _ = new RtcpTransmissionSchedule(100, 200, true, random: () => sample));
    }
}
