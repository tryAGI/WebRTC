using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using tryAGI.WebRTC;

internal static class RetentionBenchmarks
{
    internal static async Task Run()
    {
        var mode = Environment.GetEnvironmentVariable("TRYAGI_RETENTION_MODE") ?? "peer_open";
        if (mode is not ("empty_control" or "identity_only" or "crypto_only" or "peer_audio" or "peer_open")) throw new ArgumentException("Unknown retention mode");
        var traceSetting = Environment.GetEnvironmentVariable("TRYAGI_RETENTION_TRACE") ?? "0";
        if (traceSetting is not ("0" or "1")) throw new ArgumentException("Unknown retention trace setting");
        var trace = traceSetting == "1";
        var profilingSetting = Environment.GetEnvironmentVariable("TRYAGI_RETENTION_PROFILE") ?? "0";
        if (profilingSetting is not ("0" or "1")) throw new ArgumentException("Unknown retention profiling setting");
        var profiling = profilingSetting == "1";
        var wavesSetting = Environment.GetEnvironmentVariable("TRYAGI_RETENTION_WAVES") ?? "24";
        if (wavesSetting is not ("24" or "64")) throw new ArgumentException("Unknown retention wave count");
        var waves = wavesSetting == "64" ? 64 : 24;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(waves == 64 ? 8 : 3));
        Console.WriteLine($"# process_id={Environment.ProcessId}; mode={mode}; trace={trace}; profile=AeadAes256Gcm; warmup_waves=4; waves={waves}; pairs=2; live_idle_ms=2000; tiered_compilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation") ?? "default"}");
        Console.WriteLine("mode,trace,wave,phase,elapsed_ms,managed_heap,managed_committed,fragmented_bytes,rss,private_bytes,handles,alive_disposed_peers");
        // Warm the redirected-console handshake before creating or measuring peers.
        if (profiling) await ProfileBarrier("ready", -5, deadline.Token);
        var weak = new List<WeakReference<PeerConnection>>();
        var start = Stopwatch.GetTimestamp();
        for (var wave = -4; wave < waves; wave++)
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
            if (profiling && (wave is 2 or 16 || waves == 64 && wave is 32 or 63)) await ProfileBarrier("disposed", wave, deadline.Token);
        }
    }
    private static async Task ProfileBarrier(string phase, int wave, CancellationToken ct)
    {
        if (!Console.IsInputRedirected) throw new IOException("Profiling requires an owned redirected-input controller");
        Console.WriteLine($"# profile_barrier={phase};wave={wave};alive_disposed_peers=0");
        Console.Out.Flush();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Console's synchronized reader can block before returning a task. Isolate
        // that read so cancellation bounds this await even when stdin stays open.
        var reply = await Task.Run(() => Console.In.ReadLine(), CancellationToken.None).WaitAsync(deadline.Token);
        if (reply != $"continue:{wave}") throw new IOException("Profiling controller ended or supplied an invalid acknowledgement");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Alive(WeakReference<PeerConnection> peer) => peer.TryGetTarget(out _);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<PeerConnection>[]> Cohort(string mode, bool trace, int wave, CancellationToken ct)
    {
        if (mode == "crypto_only")
        {
            // Four peers have eight directional SRTP contexts. Fresh synthetic
            // keys per pair preserve nonce uniqueness across every cohort.
            for (var pair = 0; pair < 4; pair++)
            {
                var key = new byte[32]; var salt = new byte[12];
                Random.Shared.NextBytes(key); Random.Shared.NextBytes(salt);
                using var sender = new SrtpContext(SrtpProfile.AeadAes256Gcm, SrtpDirection.Send, key, salt);
                using var receiver = new SrtpContext(SrtpProfile.AeadAes256Gcm, SrtpDirection.Receive, key, salt);
                var plain = new byte[1012]; plain[0] = 0x80; plain[1] = 111; plain[11] = 42;
                var encrypted = new byte[plain.Length + sender.RtpOverhead]; var recovered = new byte[plain.Length];
                for (ushort i = 0; i < 128; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(2), i);
                    if (!sender.TryProtectRtp(plain, encrypted, out var length) ||
                        !receiver.TryUnprotectRtp(encrypted.AsSpan(0, length), recovered, out length) ||
                        !plain.AsSpan().SequenceEqual(recovered.AsSpan(0, length))) throw new IOException("Crypto retention control failed");
                }
            }
            await Task.Delay(2000, ct);
            return [];
        }
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
