using System.Diagnostics;

// Allocation warm-up alone does not wait for background tiered compilation.
// Observe five recent batch times after at least 500 ms, with a 3 s/128 batch
// bound. Stability is an observation, never proof that the JIT has finished.
internal static class TimingWarmup
{
    internal static void Run(string name, Action<int> exercise, int count)
    {
        var recent = new double[5];
        var started = Stopwatch.GetTimestamp();
        var stable = false;
        var rounds = 0;
        double minimum = 0, maximum = 0;
        while (rounds < 128 && Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
        {
            var batch = Stopwatch.GetTimestamp();
            exercise(count);
            recent[rounds % recent.Length] = Stopwatch.GetElapsedTime(batch).TotalNanoseconds / count;
            rounds++;
            minimum = maximum = recent[0];
            for (var i = 1; i < Math.Min(rounds, recent.Length); i++)
            {
                minimum = Math.Min(minimum, recent[i]); maximum = Math.Max(maximum, recent[i]);
            }
            if (rounds >= recent.Length)
            {
                stable = minimum > 0 && maximum / minimum <= 1.10 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 500;
                if (stable) break;
            }
            Thread.Sleep(10);
        }
        Console.WriteLine($"#warmup,{name},{rounds},{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1},{minimum:F1},{maximum:F1},{stable}");
    }
}
