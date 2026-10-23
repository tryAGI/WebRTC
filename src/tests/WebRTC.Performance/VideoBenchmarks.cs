using System.Buffers.Binary;
using System.Diagnostics;
using tryAGI.WebRTC;

internal static class VideoBenchmarks
{
    internal static void Run()
    {
        Console.WriteLine("codec,sample,frames,managed_bytes_per_frame,ns_per_frame");
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            using var assembler = new VideoFrameAssembler(new() { Codec = codec, PayloadType = 96, SynchronizationSource = 42 });
            var first = new byte[514]; var last = new byte[514];
            foreach (var p in new[] { first, last }) { p[0] = 0x80; p[1] = 96; p[11] = 42; }
            last[1] |= 128;
            var offset = codec == VideoCodec.H264 ? 14 : 13;
            first[12] = codec == VideoCodec.H264 ? (byte)0x7c : (byte)0x10;
            last[12] = codec == VideoCodec.H264 ? (byte)0x7c : (byte)0;
            if (codec == VideoCodec.H264) { first[13] = 0x85; last[13] = 0x45; }
            Array.Fill(first, (byte)1, offset, 500); Array.Fill(last, (byte)2, offset, 500);
            var expected = new byte[codec == VideoCodec.H264 ? 1005 : 1000];
            var prefix = codec == VideoCodec.H264 ? 5 : 0;
            if (codec == VideoCodec.H264)
            {
                expected[3] = 1; expected[4] = 0x65;
                if (assembler.Push(Convert.FromHexString("80e00000000000000000002a6155")).Count != 0)
                    throw new IOException("H264 boundary priming unexpectedly emitted a frame");
            }
            Array.Fill(expected, (byte)1, prefix, 500); Array.Fill(expected, (byte)2, prefix + 500, 500);
            var sequence = 1; uint timestamp = 9000;
            Exercise(1000);
            for (var sample = 0; sample < 5; sample++)
            {
                const int count = 2000;
                var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                Exercise(count);
                var elapsed = Stopwatch.GetElapsedTime(start); var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine($"{codec},{sample},{count},{bytes / (double)count:F2},{elapsed.TotalNanoseconds / count:F1}");
            }
            void Exercise(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(4), timestamp);
                    BinaryPrimitives.WriteUInt32BigEndian(last.AsSpan(4), timestamp); timestamp += 9000;
                    BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(2), checked((ushort)sequence++));
                    BinaryPrimitives.WriteUInt16BigEndian(last.AsSpan(2), checked((ushort)sequence++));
                    if (assembler.Push(first.AsSpan(0, offset + 500)).Count != 0) throw new IOException("Partial video frame emitted");
                    var frames = assembler.Push(last.AsSpan(0, offset + 500));
                    if (frames.Count != 1 || !frames[0].Payload.AsSpan().SequenceEqual(expected))
                        throw new IOException("Video benchmark reassembly mismatch");
                }
            }
        }
    }
}
