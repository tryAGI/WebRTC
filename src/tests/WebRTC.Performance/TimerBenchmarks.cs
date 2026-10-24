using System.Diagnostics;

// Framework-only timing control: no peers, sockets or crypto. Report every
// observed wake gap; a slow producer must not be mistaken for protected audio.
internal static class TimerBenchmarks
{
    internal static async Task Run()
    {
        Console.WriteLine("timer,sample,requested_us,elapsed_us");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (var timer in new[] { "delay", "periodic", "sleep" })
        {
            using var periodic = timer == "periodic" ? new PeriodicTimer(TimeSpan.FromMilliseconds(20)) : null;
            // Periodic control has its own schedule; do not let an unused timer
            // accumulate ticks throughout the preceding delay/sleep control.
            for (var sample = 0; sample < 100; sample++)
            {
                bound.Token.ThrowIfCancellationRequested();
                var start = Stopwatch.GetTimestamp();
                if (timer == "delay") await Task.Delay(20, bound.Token);
                else if (periodic is not null)
                {
                    if (!await periodic.WaitForNextTickAsync(bound.Token)) throw new IOException("Timer control ended early");
                }
                else Thread.Sleep(20);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                Console.WriteLine($"{timer},{sample},20000,{elapsed:F3}");
            }
        }
    }
}
