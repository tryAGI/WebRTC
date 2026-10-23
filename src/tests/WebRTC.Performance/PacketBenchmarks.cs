using System.Diagnostics;
using tryAGI.WebRTC;

internal static class PacketBenchmarks
{
    internal static void Run()
    {
        Console.WriteLine("operation,sample,operations,managed_bytes_per_op,ns_per_op,managed_total_bytes");
        foreach (var operation in PacketAllocationChecks.Operations)
        {
            var fixture = PacketAllocationChecks.Fixture(operation);
            TimingWarmup.Run(operation, count => PacketAllocationChecks.Exercise(operation, fixture, count), 20000);
            for (var sample = 0; sample < 7; sample++)
            {
                const int count = 20000;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                PacketAllocationChecks.Exercise(operation, fixture, count);
                var elapsed = Stopwatch.GetElapsedTime(started);
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Console.WriteLine($"{operation},{sample},{count},{bytes / (double)count:F2},{elapsed.TotalNanoseconds / count:F1},{bytes}");
            }
        }
    }
}
