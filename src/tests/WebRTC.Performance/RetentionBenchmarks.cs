using System.Diagnostics;
using System.Runtime.CompilerServices;
using tryAGI.WebRTC;

internal static class RetentionBenchmarks
{
    internal static async Task Run()
    {
        var mode = Environment.GetEnvironmentVariable("TRYAGI_RETENTION_MODE") ?? "peer_open";
        if (mode is not ("empty_control" or "identity_only" or "peer_audio" or "peer_open")) throw new ArgumentException("Unknown retention mode");
        var traceSetting = Environment.GetEnvironmentVariable("TRYAGI_RETENTION_TRACE") ?? "0";
        if (traceSetting is not ("0" or "1")) throw new ArgumentException("Unknown retention trace setting");
        var trace = traceSetting == "1";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Console.WriteLine($"# process_id={Environment.ProcessId}; mode={mode}; trace={trace}; profile=AeadAes256Gcm; warmup_waves=4; waves=24; pairs=2; live_idle_ms=2000; normal_tiered_compilation");
        Console.WriteLine("mode,trace,wave,phase,elapsed_ms,managed_heap,managed_committed,fragmented_bytes,rss,private_bytes,handles,alive_disposed_peers");
        var weak = new List<WeakReference<PeerConnection>>();
        var start = Stopwatch.GetTimestamp();
        for (var wave = -4; wave < 24; wave++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            weak.AddRange(await Cohort(mode, trace, wave, deadline.Token));
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
                if (!weak.Any(Alive)) break;
                await Task.Delay(20, deadline.Token);
            }
            var alive = weak.Count(Alive);
            if (wave >= 0) Snapshot(mode, trace, wave, "disposed", Stopwatch.GetElapsedTime(start), alive);
            if (alive != 0) throw new IOException("Disposed retention peer remains rooted");
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Alive(WeakReference<PeerConnection> peer) => peer.TryGetTarget(out _);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<PeerConnection>[]> Cohort(string mode, bool trace, int wave, CancellationToken ct)
    {
        if (mode is "empty_control" or "identity_only")
        {
            if (mode == "identity_only") for (var i = 0; i < 4; i++) { using var identity = DtlsIdentity.Generate(); }
            await Task.Delay(2000, ct);
            return [];
        }
        var pairs = new List<AudioPeerPair>();
        try
        {
            for (var i = 0; i < 2; i++) pairs.Add(await AudioPeerPair.Create(SrtpProfile.AeadAes256Gcm, trace, mode == "peer_open", 1000, i, ct));
            await Task.WhenAll(pairs.Select(p => p.Exchange(64)));
            if (mode == "peer_open") await Task.WhenAll(pairs.Select(p => p.WarmChannel()));
            await Task.Delay(2000, ct);
            await Task.WhenAll(pairs.Select(p => p.Exchange(64)));
            foreach (var pair in pairs) { pair.Check(); await pair.CancelledSend(); }
            return pairs.SelectMany(p => new[] { new WeakReference<PeerConnection>(p.Left), new WeakReference<PeerConnection>(p.Right) }).ToArray();
        }
        finally { await Task.WhenAll(pairs.Select(p => p.DisposeAsync().AsTask())); }
    }
    private static void Snapshot(string mode, bool trace, int wave, string phase, TimeSpan elapsed, int alive)
    {
        var heap = GC.GetTotalMemory(true); var info = GC.GetGCMemoryInfo(); using var process = Process.GetCurrentProcess();
        var memory = process.PrivateMemorySize64;
        Console.WriteLine($"{mode},{trace},{wave},{phase},{elapsed.TotalMilliseconds:F3},{heap},{info.TotalCommittedBytes},{info.FragmentedBytes},{process.WorkingSet64},{(memory>0?memory.ToString():"unavailable")},{(OperatingSystem.IsMacOS()?"unavailable":process.HandleCount.ToString())},{alive}");
    }
}
