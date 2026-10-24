using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

// Framework-only attribution. No socket, peer, timer wait or protocol replay.
// Reopen one synthetic sealed message; no new ciphertext/nonce reuse is created.
internal static class FrameworkRejectionBenchmarks
{
    internal static void Run()
    {
        var selected = Environment.GetEnvironmentVariable("TRYAGI_COST_MODE") ?? "all";
        if (selected is not ("all" or "valid" or "bad-tag" or "throw")) throw new ArgumentException("Unknown rejection-cost mode");
        using var aes = new AesGcm(new byte[16], 16);
        var plain = Enumerable.Repeat((byte)0x27, 60).ToArray(); var cipher = new byte[60]; var output = new byte[60];
        var nonce = new byte[12]; var aad = new byte[12]; var tag = new byte[16];
        aes.Encrypt(nonce, plain, cipher, tag, aad);
        var badTag = (byte[])tag.Clone(); badTag[0] ^= 1;
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("mode,sample,operations,accepted,rejected,managed_total_bytes,ns_per_op,cpu_ms,elapsed_ms");
        foreach (var mode in selected == "all" ? new[] { "valid", "bad-tag", "throw" } : new[] { selected })
        {
            _ = Exercise(mode, 1000, aes, nonce, cipher, tag, badTag, output, aad);
            for (var sample = 0; sample < 7; sample++)
            {
                process.Refresh(); var cpu = process.TotalProcessorTime;
                var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                long operations = 0, accepted = 0, rejected = 0;
                do
                {
                    var counts = Exercise(mode, 128, aes, nonce, cipher, tag, badTag, output, aad);
                    operations += 128; accepted += counts.Accepted; rejected += counts.Rejected;
                } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 500);
                var elapsed = Stopwatch.GetElapsedTime(start); var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                process.Refresh(); var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                if (mode == "valid")
                {
                    if (accepted != operations || rejected != 0 || !output.AsSpan().SequenceEqual(plain)) throw new IOException("Framework valid-open control failed");
                }
                else if (rejected != operations || accepted != 0 || (mode == "bad-tag" && output.AsSpan().ContainsAnyExcept((byte)0)))
                    throw new IOException("Framework rejection control failed");
                Console.WriteLine($"{mode},{sample},{operations},{accepted},{rejected},{allocated},{elapsed.TotalNanoseconds / operations:F3},{cpuMs:F3},{elapsed.TotalMilliseconds:F3}");
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Accepted, long Rejected) Exercise(string mode, int count, AesGcm aes,
        byte[] nonce, byte[] cipher, byte[] tag, byte[] badTag, byte[] output, byte[] aad)
    {
        long accepted = 0, rejected = 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                if (mode == "throw") ThrowSynthetic();
                else aes.Decrypt(nonce, cipher, mode == "valid" ? tag : badTag, output, aad);
                accepted++;
            }
            catch (AuthenticationTagMismatchException) { rejected++; }
        }
        return (accepted, rejected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSynthetic() => throw new AuthenticationTagMismatchException("Synthetic diagnostic throw");
}
