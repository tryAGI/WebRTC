using System.Buffers.Binary;
using tryAGI.WebRTC;

internal static class VideoTests
{
    internal static VideoFrameAssemblerOptions Options(VideoCodec codec = VideoCodec.H264) => new()
    { Codec = codec, PayloadType = 96, SynchronizationSource = 42 };
    private static void Check(bool value, string reason = "Video assertion failed") { if (!value) throw new IOException(reason); }
    internal static byte[] Packet(ushort sequence, uint timestamp, bool marker, byte[] payload, uint source = 42, byte pt = 96)
    {
        var p = new byte[12 + payload.Length]; p[0] = 0x80; p[1] = (byte)(pt | (marker ? 128 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), sequence); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), source); payload.CopyTo(p, 12); return p;
    }
    private static void Prime(VideoFrameAssembler receiver, ushort sequence = 99)
    { Check(receiver.Push(Packet(sequence, 1, true, [0x61, 0x55])).Count == 0); }
    internal static void H264()
    {
        using var receiver = new VideoFrameAssembler(Options()); Prime(receiver, 65532);
        // Own synthetic SPS/PPS aggregation, fragmented IDR and trailing SEI; reordered over sequence rollover.
        byte[][] payloads = [[0x78, 0, 2, 0x67, 0x22, 0, 2, 0x68, 0x33], [0x7C, 0x85, 0x44], [0x7C, 0x25], [0x7C, 0x45, 0x66], [0x06, 0x77]];
        var packets = payloads.Select((p, i) => Packet(unchecked((ushort)(65533 + i)), uint.MaxValue - 9, i == 4, p)).ToArray();
        IReadOnlyList<EncodedVideoFrame> result = [];
        foreach (var i in new[] { 4, 2, 0, 3, 1 }) { var frames = receiver.Push(packets[i]); if (frames.Count > 0) result = frames; }
        Check(result.Count == 1 && result[0].IsKeyFrame && result[0].FirstSequenceNumber == 65533 && result[0].LastSequenceNumber == 1);
        Check(result[0].Timestamp == uint.MaxValue - 9 && result[0].Payload.SequenceEqual(Convert.FromHexString("00000001672200000001683300000001654466000000010677")));
        Check(receiver.Push(packets[4]).Count == 0);
        Check(receiver.Push(Packet(2, 0, true, [0x61, 0x88]))[0].Payload.SequenceEqual(Convert.FromHexString("000000016188")));
        Check(receiver.GetDiagnostics() is { CompletedFrames: 2, BufferedBytes: 0, BufferedFrames: 0 });
        using var modeZero = new VideoFrameAssembler(Options() with { H264PacketizationMode = 0 }); Prime(modeZero);
        Check(modeZero.Push(Packet(100, 2, true, [0x65, 0x44])).Single().IsKeyFrame);
        Check(modeZero.Push(Packet(101, 3, true, [0x7C, 0x85, 0x44])).Count == 0);
    }
    internal static void Vp8()
    {
        using var receiver = new VideoFrameAssembler(Options(VideoCodec.Vp8));
        // Extended descriptor: long PictureID, TL0 index, TID/key index; reserved bits are ignored.
        Check(receiver.Push(Packet(11, 7, true, [0xC8, 0xFF, 0x80, 7, 3, 0x45, 0x99])).Count == 0);
        var frame = receiver.Push(Packet(10, 7, false, [0xD8, 0xFF, 0x80, 7, 3, 0x45, 0x11, 0x22, 0x33])).Single();
        Check(!frame.IsKeyFrame && frame.Payload.SequenceEqual(new byte[] { 0x11, 0x22, 0x33, 0x99 }));
        var key = receiver.Push(Packet(12, 8, true, [0x10, 0, 0, 0, 0x9D, 1, 0x2A, 16, 0, 16, 0])).Single();
        Check(key.IsKeyFrame && key.Payload.Length == 10);
        Check(receiver.Push(Packet(14, 9, false, [0x11, 0x44])).Count == 0);
        Check(receiver.Push(Packet(15, 9, true, [0x01, 0x55])).Count == 0);
        frame = receiver.Push(Packet(13, 9, false, [0x10, 0x11, 0x22, 0x33])).Single();
        Check(frame.Payload.SequenceEqual(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 }));
    }
    internal static void LossAndDuplicates()
    {
        using var receiver = new VideoFrameAssembler(Options()); Prime(receiver);
        var start = Packet(100, 2, false, [0x7C, 0x85, 1]);
        Check(receiver.Push(start).Count == 0); Check(receiver.Push(start).Count == 0);
        Check(receiver.Push(Packet(102, 2, true, [0x7C, 0x45, 3])).Count == 0);
        Check(receiver.GetDiagnostics().DuplicatePackets == 1);
        Check(receiver.Push(Packet(103, 3, true, [0x61, 4])).Count == 1);
        Check(receiver.Push(Packet(101, 2, false, [0x7C, 5, 2])).Count == 0);
        Check(receiver.GetDiagnostics() is { CompletedFrames: 1, DroppedFrames: 2, BufferedBytes: 0 });
        // Initial H264 may have a lost SPS/NAL prefix; never emit its surviving IDR suffix as a complete AU.
        using var initial = new VideoFrameAssembler(Options());
        Check(initial.Push(Packet(20, 1, false, [0x7C, 0x85, 1])).Count == 0);
        Check(initial.Push(Packet(21, 1, true, [0x7C, 0x45, 2])).Count == 0);
        Check(initial.Push(Packet(22, 2, true, [0x65, 3])).Count == 1);
        using var collision = new VideoFrameAssembler(Options(VideoCodec.Vp8));
        collision.Push(Packet(30, 1, false, [0x10, 1, 2, 3]));
        Check(collision.Push(Packet(30, 2, true, [0x10, 1, 2, 3])).Count == 0 && collision.GetDiagnostics().BufferedFrames == 0);
    }
    internal static void Malformed()
    {
        byte[][] bad = [[], [0], [0x80], [0x79, 1], [0x7A, 1], [0x7B, 1], [0x7D, 1], [0x7C], [0x7C, 0xC5], [0x7C, 0x80],
            [0x78], [0x78, 0, 0], [0x78, 0, 2, 0x65], [0x78, 0, 1, 0x78], [0x78, 0, 1, 0xFC], [0x65, 1, 2]];
        for (var i = 0; i < bad.Length - 1; i++)
        {
            using var receiver = new VideoFrameAssembler(Options()); Prime(receiver);
            Check(receiver.Push(Packet(100, 2, true, bad[i])).Count == 0, $"H264 hostile {i}");
            Check(receiver.Push(Packet(101, 3, true, [0x61, 1])).Count == 1, "Malformed marker prevented next-frame recovery");
        }
        foreach (var payloads in new byte[][][] {
            [[0x7C, 0x85, 1], [0x5C, 0x45, 2]], [[0x7C, 0x85, 1], [0x7C, 0x46, 2]],
            [[0x7C, 0x85, 1], [0x61, 2]], [[0x7C, 5, 1], [0x7C, 0x45, 2]], [[0x7C, 0x85, 1], [0x7C, 0x85, 2]] })
        {
            using var receiver = new VideoFrameAssembler(Options()); Prime(receiver);
            receiver.Push(Packet(100, 2, false, payloads[0])); Check(receiver.Push(Packet(101, 2, true, payloads[1])).Count == 0);
        }
        foreach (var p in new byte[][] { [0x90], [0x90, 0x80], [0x90, 0x80, 0x80], [0x90, 0x40, 1, 1], [0x10, 0, 0], [0x10, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1] })
        { using var receiver = new VideoFrameAssembler(Options(VideoCodec.Vp8)); Check(receiver.Push(Packet(1, 1, true, p)).Count == 0); }
        using var mismatched = new VideoFrameAssembler(Options(VideoCodec.Vp8));
        mismatched.Push(Packet(1, 1, false, [0x90, 0x80, 5, 1, 2, 3]));
        Check(mismatched.Push(Packet(2, 1, true, [0x80, 0x80, 6, 4])).Count == 0);
    }
    internal static void Routing()
    {
        using var receiver = new VideoFrameAssembler(Options(VideoCodec.Vp8) with { Mid = "video", MidExtensionId = 1 });
        var plain = Packet(1, 1, true, [0x10, 1, 2, 3]);
        Check(receiver.Push(Packet(1, 1, true, [0x10, 1, 2, 3], 43)).Count == 0);
        Check(receiver.Push(Packet(1, 1, true, [0x10, 1, 2, 3], pt: 97)).Count == 0);
        byte[] Extended(string mid, ushort seq)
        {
            var p = new byte[plain.Length + 12]; plain.AsSpan(0, 12).CopyTo(p); p[0] |= 16;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), seq); p[12] = 0xBE; p[13] = 0xDE; p[15] = 2;
            p[16] = 0x14; System.Text.Encoding.ASCII.GetBytes(mid).CopyTo(p, 17); plain.AsSpan(12).CopyTo(p.AsSpan(24)); return p;
        }
        Check(receiver.Push(Extended("other", 1)).Count == 0);
        var malformed = Extended("video", 1); malformed[16] = 0x1F; Check(receiver.Push(malformed).Count == 0);
        Check(receiver.GetDiagnostics() is { RejectedPackets: 4, BufferedBytes: 0 });
        Check(receiver.Push(Extended("video", 1)).Count == 1);
        // An absent MID is permitted only because this owner already pins the authorized remote source and payload type.
        Check(receiver.Push(Packet(2, 2, true, [0x10, 1, 2, 3])).Count == 1);
    }
    internal static void BoundsAndExpiry()
    {
        var time = new Clock();
        using var receiver = new VideoFrameAssembler(Options(VideoCodec.Vp8) with
        { MaximumFrames = 2, MaximumPacketsPerFrame = 2, MaximumFrameBytes = 64, MaximumBufferedBytes = 64 }, time);
        receiver.Push(Packet(1, 1, false, [0x10, 1, 2, 3])); receiver.Push(Packet(2, 2, false, [0x10, 1, 2, 3]));
        receiver.Push(Packet(3, 3, false, [0x10, 1, 2, 3])); Check(receiver.GetDiagnostics() is { BufferedFrames: 2, DroppedFrames: 1 });
        time.Advance(251); receiver.Expire(); Check(receiver.GetDiagnostics() is { BufferedBytes: 0, BufferedFrames: 0, DroppedFrames: 3 });
        Check(receiver.Push(Packet(4, 3, true, [0, 4])).Count == 0);
        receiver.Push(Packet(5, 4, false, [0x10, 1, 2, 3])); receiver.Push(Packet(6, 4, false, [0, 4]));
        Check(receiver.Push(Packet(7, 4, true, [0, 5])).Count == 0 && receiver.GetDiagnostics().BufferedBytes == 0);
        Check(receiver.Push(Packet(8, 5, true, [0x10, .. new byte[65]])).Count == 0);
        using var expansion = new VideoFrameAssembler(Options() with { MaximumFrameBytes = 64, MaximumBufferedBytes = 64 }); Prime(expansion);
        Check(expansion.Push(Packet(100, 2, true, [0x78, .. Enumerable.Range(0, 13).SelectMany(_ => new byte[] { 0, 1, 0x65 })])).Count == 0);
        Check(expansion.GetDiagnostics() is { BufferedBytes: 6, BufferedFrames: 1, RejectedPackets: 1 });
        using var total = new VideoFrameAssembler(Options(VideoCodec.Vp8) with { MaximumFrameBytes = 64, MaximumBufferedBytes = 64 });
        var large = new byte[] { 0x10, 1, 2, 3 }.Concat(Enumerable.Repeat((byte)7, 30)).ToArray();
        total.Push(Packet(1, 1, false, large));
        Check(total.Push(Packet(2, 2, true, large)).Count == 0);
        Check(total.GetDiagnostics() is { BufferedFrames: 1, BufferedBytes: 34, RejectedPackets: 1 });
        Check(total.Push(Packet(3, 3, true, [0x10, 1, 2, 3])).Count == 1 && total.GetDiagnostics().BufferedBytes == 0);
        using var packetLimit = new VideoFrameAssembler(Options(VideoCodec.Vp8) with { MaximumPacketBytes = 64 });
        Check(packetLimit.Push(Packet(1, 1, true, [0x10, .. new byte[53]])).Count == 0 && packetLimit.GetDiagnostics().BufferedFrames == 0);
        // New fragments and identical duplicates must not renew a frame's absolute age.
        receiver.Reset(); receiver.Push(Packet(1, 1, false, [0x10, 1, 2, 3])); time.Advance(249);
        receiver.Push(Packet(1, 1, false, [0x10, 1, 2, 3])); time.Advance(2); receiver.Expire();
        Check(receiver.GetDiagnostics().BufferedBytes == 0);
        // A forward discontinuity frees stale frames and reestablishes the VP8 boundary.
        receiver.Reset(); receiver.Push(Packet(1, 1, false, [0x10, 1, 2, 3]));
        Check(receiver.Push(Packet(9000, 9, true, [0x10, 1, 2, 3])).Count == 1 && receiver.GetDiagnostics().BufferedBytes == 0);
    }
    internal static void LifetimeAndCorpus()
    {
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            using var receiver = new VideoFrameAssembler(Options(codec)); var random = new Random(72815);
            for (var i = 0; i < 12000; i++)
            {
                var bytes = new byte[random.Next(0, 256)]; random.NextBytes(bytes);
                if (i % 3 == 0) bytes = Packet(unchecked((ushort)i), (uint)i, i % 2 == 0, bytes);
                receiver.Push(bytes);
                var d = receiver.GetDiagnostics(); Check(d.BufferedFrames <= 4 && d.BufferedBytes <= 4 * 1024 * 1024 && d.BufferedPackets <= 4096);
            }
            receiver.Reset(); Check(receiver.GetDiagnostics().BufferedBytes == 0);
            receiver.Dispose(); receiver.Dispose(); Check(receiver.GetDiagnostics().BufferedPackets == 0);
            try { receiver.Push([]); throw new IOException("Disposed assembler accepted input"); } catch (ObjectDisposedException) { }
        }
        foreach (var options in new[] { Options() with { SynchronizationSource = 0 }, Options() with { MaximumFrames = 9 }, Options() with { H264PacketizationMode = 2 },
            Options() with { MaximumPacketsPerFrame = 4097 }, Options() with { Mid = "bad\r\nmid", MidExtensionId = 1 } })
        { try { using var bad = new VideoFrameAssembler(options); throw new IOException("Invalid options admitted"); } catch (ArgumentOutOfRangeException) { } }
    }
    private sealed class Clock : TimeProvider
    {
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        internal void Advance(long ms) => _stamp += ms;
    }
}
