using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using tryAGI.WebRTC;

internal static class LifecycleBenchmarks
{
    internal static async Task Cold()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Console.WriteLine("scenario,profile,data_channels,sample,allocation_scope,managed_bytes,total_ms,setup_ms,first_bidirectional_ms,dispose_ms");
        foreach (var profile in Enum.GetValues<SrtpProfile>())
        foreach (var channels in new[] { false, true })
        for (var sample = 0; sample < 8; sample++)
        {
            var allocated = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
            var pair = await AudioPeerPair.Create(profile, false, channels, 60, sample, deadline.Token);
            var setup = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var first = Stopwatch.GetTimestamp();
            double delivery = 0, disposal = 0;
            try { await pair.Exchange(1); pair.Check(); delivery = Stopwatch.GetElapsedTime(first).TotalMilliseconds; }
            finally { var stop = Stopwatch.GetTimestamp(); await pair.DisposeAsync(); disposal = Stopwatch.GetElapsedTime(stop).TotalMilliseconds; }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine($"peer_connection,{profile},{channels},{sample},process,{GC.GetTotalAllocatedBytes(true)-allocated},{elapsed:F3},{setup:F3},{delivery:F3},{disposal:F3}");
            foreach (var (label, peer) in new[] { ("left", pair.Left), ("right", pair.Right) })
            foreach (var phase in peer.GetEstablishmentEvidence().Phases)
                Console.WriteLine($"# phase,{profile},{channels},{sample},{label},{phase.Phase},{phase.Status},{phase.Elapsed?.TotalMilliseconds:F3},{phase.Retransmissions}");
            Console.WriteLine($"# local_preparation,{profile},{channels},{sample},construct_ms={pair.ConstructionMilliseconds:F3},signaling_ms={pair.SignalingMilliseconds:F3}");
        }
        Console.WriteLine("# peer samples are new connections; sample 0 includes first-use effects; later samples share warmed JIT/native caches; not process-isolated startup");
        FreshKeyGenerations();
    }
    private static void FreshKeyGenerations()
    {
        foreach (var profile in Enum.GetValues<SrtpProfile>())
        for (var sample = 0; sample < 8; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            var key = RandomNumberGenerator.GetBytes(profile == SrtpProfile.AeadAes256Gcm ? 32 : 16);
            var salt = RandomNumberGenerator.GetBytes(profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12);
            try
            {
                using var sender = new SrtpContext(profile, SrtpDirection.Send, key, salt);
                using var receiver = new SrtpContext(profile, SrtpDirection.Receive, key, salt);
                var plain = new byte[72]; plain[0] = 0x80; plain[1] = 111;
                BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(8), 42);
                var packet = new byte[plain.Length + sender.RtpOverhead]; var decoded = new byte[plain.Length];
                if (!sender.TryProtectRtp(plain, packet, out var length) || !receiver.TryUnprotectRtp(packet.AsSpan(0, length), decoded, out var written) ||
                    written != plain.Length || !plain.AsSpan().SequenceEqual(decoded)) throw new IOException("Fresh key generation failed");
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(salt); }
            Console.WriteLine($"fresh_srtp_generation,{profile},not_applicable,{sample},current_thread,{GC.GetAllocatedBytesForCurrentThread()-before},{Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3},not_applicable,not_applicable,not_applicable");
        }
        Console.WriteLine("# fresh directional context/key generation includes RNG/disposal; this library does not negotiate in-session DTLS rekey");
    }
    internal static async Task Audio()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Console.WriteLine("profile,trace,payload_bytes,sample,delivered_packets,process_bytes_per_packet,ns_per_packet,p50_batch_ms,p95_batch_ms,p99_batch_ms");
        foreach (var profile in Enum.GetValues<SrtpProfile>())
        foreach (var trace in new[] { false, true })
        foreach (var payload in new[] { 60, 1000 })
        {
            await using var pair = await AudioPeerPair.Create(profile, trace, false, payload, 0, deadline.Token);
            await pair.Exchange(64);
            for (var sample = 0; sample < 5; sample++)
            {
                var latency = new double[64]; var before = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < latency.Length; i++)
                {
                    var batch = Stopwatch.GetTimestamp(); await pair.Exchange(8);
                    latency[i] = Stopwatch.GetElapsedTime(batch).TotalMilliseconds;
                }
                var elapsed = Stopwatch.GetElapsedTime(start); var allocated = GC.GetTotalAllocatedBytes(true) - before;
                pair.Check(); Array.Sort(latency);
                const int delivered = 64 * 8 * 2;
                Console.WriteLine($"{profile},{trace},{payload},{sample},{delivered},{allocated/(double)delivered:F2},{elapsed.TotalNanoseconds/delivered:F1},{latency[31]:F3},{latency[60]:F3},{latency[63]:F3}");
            }
        }
    }
    internal static async Task Soak()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Console.WriteLine("profile,trace,cycle,phase,delivered_packets,managed_heap,working_set,private_bytes,handles,alive_disposed_peers,intentional_slow_reader_drops");
        foreach (var profile in Enum.GetValues<SrtpProfile>())
        foreach (var trace in new[] { false, true })
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var weak = await Cohort(profile, trace, cycle, deadline.Token);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
                if (!weak.Any(Alive)) break;
                await Task.Delay(20, deadline.Token);
            }
            var alive = weak.Count(Alive);
            Snapshot(profile, trace, cycle, "disposed", 3 * 8 * 256 * 2, alive, 0);
            if (alive != 0) throw new IOException("Disposed audio peer remained strongly rooted after bounded collection");
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Alive(WeakReference<PeerConnection> peer) => peer.TryGetTarget(out _);
    private static async Task<WeakReference<PeerConnection>[]> Cohort(SrtpProfile profile, bool trace, int cycle, CancellationToken ct)
    {
        var pairs = new List<AudioPeerPair>();
        try
        {
            for (var i = 0; i < 3; i++) pairs.Add(await AudioPeerPair.Create(profile, trace, false, 1000, i, ct));
            for (var epoch = 0; epoch < 8; epoch++)
            {
                await Task.WhenAll(pairs.Select(p => p.Exchange(256)));
                foreach (var pair in pairs) { pair.Check(); await pair.CancelledSend(); }
                if (epoch is 0 or 7) Snapshot(profile, trace, cycle, "live_"+epoch, 3*(epoch+1)*256*2, 0, 0);
            }
            var drops = await pairs[0].SlowReader(); pairs[0].Check();
            Snapshot(profile, trace, cycle, "bounded_eviction", 3*8*256*2, 0, drops);
            return pairs.SelectMany(p => new[] { new WeakReference<PeerConnection>(p.Left), new WeakReference<PeerConnection>(p.Right) }).ToArray();
        }
        finally { await Task.WhenAll(pairs.Select(p => p.DisposeAsync().AsTask())); }
    }
    private static void Snapshot(SrtpProfile profile, bool trace, int cycle, string phase, int packets, int alive, long drops)
    {
        var heap = GC.GetTotalMemory(true); using var process = Process.GetCurrentProcess();
        var memory = process.PrivateMemorySize64;
        Console.WriteLine($"{profile},{trace},{cycle},{phase},{packets},{heap},{process.WorkingSet64},{(memory>0?memory.ToString():"unavailable")},{(OperatingSystem.IsMacOS()?"unavailable":process.HandleCount.ToString())},{alive},{drops}");
    }
}
