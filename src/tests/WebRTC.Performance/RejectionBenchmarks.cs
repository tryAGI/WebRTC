using System.Diagnostics;
using tryAGI.WebRTC;

internal static class RejectionBenchmarks
{
    internal static void Run(bool gmac = false)
    {
        Console.WriteLine("profile,packet,packet_bytes,mode,sample,operations,managed_total_bytes,process_total_bytes,ns_per_op,cpu_ms,gen0,gen1,gen2");
        using var process = Process.GetCurrentProcess();
        foreach (var profile in (gmac ? new[] { SrtpProfile.AeadAes128Gcm, SrtpProfile.AeadAes256Gcm } : Enum.GetValues<SrtpProfile>()))
        foreach (var packet in (gmac ? new[] { "rtp-empty", "rtcp-empty", "rtcp-auth-only" } : new[] { "rtp-audio", "rtp-video", "rtcp" }))
        foreach (var mode in RejectionAllocationChecks.Modes)
        {
            var rtcp = packet.StartsWith("rtcp", StringComparison.Ordinal);
            var payload = packet.EndsWith("empty", StringComparison.Ordinal) ? 0 : packet == "rtp-video" ? 1200 : 60;
            using var fixture = new RejectionAllocationChecks(profile, rtcp, payload, mode, packet == "rtcp-auth-only");
            fixture.Exercise(1000);
            for (var sample = 0; sample < 7; sample++)
            {
                const int count = 1000;
                process.Refresh(); var cpu = process.TotalProcessorTime;
                var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
                var total = GC.GetTotalAllocatedBytes(false);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                fixture.Exercise(count);
                var elapsed = Stopwatch.GetElapsedTime(start);
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var processBytes = GC.GetTotalAllocatedBytes(false) - total;
                gen0 = GC.CollectionCount(0) - gen0; gen1 = GC.CollectionCount(1) - gen1; gen2 = GC.CollectionCount(2) - gen2;
                process.Refresh(); var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                Console.WriteLine($"{profile},{packet},{(rtcp ? payload == 0 ? 8 : 28 : 12 + payload)},{mode},{sample},{count},{bytes},{processBytes},{elapsed.TotalNanoseconds / count:F1},{cpuMs:F3},{gen0},{gen1},{gen2}");
            }
            fixture.VerifyAfterMeasurement();
        }
    }
}
