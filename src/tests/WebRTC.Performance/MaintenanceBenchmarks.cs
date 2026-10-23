using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using tryAGI.WebRTC;

internal static class MaintenanceBenchmarks
{
    internal static async Task Idle(bool channels = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Console.WriteLine("profile,trace,pairs,channel_state,sample,elapsed_ms,process_bytes_per_second,cpu_ms_per_second,threadpool_items_per_second,consent_checks,retransmissions,sctp_retransmissions,sctp_outstanding,sctp_buffered_send,sctp_buffered_receive,gen0,gen1,gen2");
        await Measure("harness_only", false, "off", [], deadline.Token);
        foreach (var profile in channels ? [SrtpProfile.AeadAes256Gcm] : Enum.GetValues<SrtpProfile>())
        foreach (var mode in channels ? new[] { "off", "associated", "open" } : ["off"])
        foreach (var trace in new[] { false, true })
        foreach (var count in new[] { 1, 3 })
        {
            var pairs = new List<AudioPeerPair>();
            try
            {
                for (var i = 0; i < count; i++) pairs.Add(await AudioPeerPair.Create(profile, trace, mode != "off", 60, i, deadline.Token));
                await Task.WhenAll(pairs.Select(p => p.Exchange(4)));
                if (mode == "open") await Task.WhenAll(pairs.Select(p => p.WarmChannel()));
                await Task.Delay(1000, deadline.Token);
                await Measure(profile.ToString(), trace, mode, pairs, deadline.Token);
                foreach (var pair in pairs) pair.Check();
            }
            finally { await Task.WhenAll(pairs.Select(p => p.DisposeAsync().AsTask())); }
        }
        static async Task Measure(string profile, bool trace, string mode, IReadOnlyList<AudioPeerPair> pairs, CancellationToken ct)
        {
            using var process = Process.GetCurrentProcess();
            for (var sample = 0; sample < 3; sample++)
            {
                long Checks() => pairs.Sum(p => p.Left.GetDiagnostics().Ice.SentChecks + p.Right.GetDiagnostics().Ice.SentChecks);
                long Retries() => pairs.Sum(p => p.Left.GetDiagnostics().Ice.Retransmissions + p.Right.GetDiagnostics().Ice.Retransmissions);
                long SctpRetries() => pairs.Sum(p => (p.Left.GetDiagnostics().Sctp?.Retransmissions ?? 0) + (p.Right.GetDiagnostics().Sctp?.Retransmissions ?? 0));
                var checks = Checks(); var retries = Retries(); var sctpRetries = SctpRetries();
                var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
                process.Refresh();
                var work = ThreadPool.CompletedWorkItemCount; var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
                await Task.Delay(2000, ct);
                var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                process.Refresh();
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var items = ThreadPool.CompletedWorkItemCount - work;
                var snapshots = pairs.SelectMany(p => new[] { p.Left.GetDiagnostics().Sctp, p.Right.GetDiagnostics().Sctp }).OfType<SctpDiagnostics>().ToArray();
                var outstanding = snapshots.Sum(p => p.OutstandingChunks); var send = snapshots.Sum(p => p.BufferedSendBytes); var receive = snapshots.Sum(p => p.BufferedReceiveBytes);
                if (outstanding != 0 || send != 0 || receive != 0) throw new IOException("Idle SCTP fixture did not drain");
                Console.WriteLine($"{profile},{trace},{pairs.Count},{mode},{sample},{seconds*1000:F3},{bytes/seconds:F2},{cpuMs/seconds:F3},{items/seconds:F2},{Checks()-checks},{Retries()-retries},{SctpRetries()-sctpRetries},{outstanding},{send},{receive},{GC.CollectionCount(0)-g0},{GC.CollectionCount(1)-g1},{GC.CollectionCount(2)-g2}");
            }
        }
    }
    private sealed record Cost(double KeyMs, double RequestMs, double CertificateMs, double VerifyMs, double DisposeMs, long Bytes);
    internal static void Identity()
    {
        Console.WriteLine("mode,sample,scope,key_and_public_export_ms,request_ms,certificate_or_generation_ms,verify_ms,dispose_ms,thread_managed_bytes");
        foreach (var mode in new[] { "legacy_private_certificate", "public_only_certificate", "runtime_identity" })
        for (var sample = 0; sample < 24; sample++)
        {
            var result = Generate(mode);
            if (mode == "runtime_identity")
                Console.WriteLine($"{mode},{sample},whole_identity,not_isolated,not_isolated,{result.CertificateMs:F3},not_isolated,{result.DisposeMs:F3},{result.Bytes}");
            else Console.WriteLine($"{mode},{sample},split_creation,{result.KeyMs:F3},{result.RequestMs:F3},{result.CertificateMs:F3},{result.VerifyMs:F3},{result.DisposeMs:F3},{result.Bytes}");
        }
    }
    private static Cost Generate(string mode)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        if (mode == "runtime_identity")
        {
            var identity = DtlsIdentity.Generate(); var made = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var stop = Stopwatch.GetTimestamp();
            try { if (identity.GetFingerprintSha256().Length != 32) throw new IOException("Invalid runtime identity"); }
            finally { stop = Stopwatch.GetTimestamp(); identity.Dispose(); }
            return new(0, 0, made, 0, Stopwatch.GetElapsedTime(stop).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread()-allocated);
        }
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        X509Certificate2? certificate = null;
        double keyMs, requestMs, certificateMs, verifyMs, disposeMs;
        try
        {
            var publicKey = key.ExportSubjectPublicKeyInfo(); keyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            var request = new CertificateRequest("CN=tryAGI.WebRTC", key, HashAlgorithmName.SHA256);
            requestMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var now = DateTimeOffset.UtcNow; start = Stopwatch.GetTimestamp();
            if (mode == "legacy_private_certificate") certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(30));
            else
            {
                var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7f; serial[0] |= 1;
                certificate = request.Create(request.SubjectName, X509SignatureGenerator.CreateForECDsa(key), now.AddMinutes(-5), now.AddDays(30), serial);
            }
            certificateMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            using var verify = certificate.GetECDsaPublicKey() ?? throw new IOException("Missing certificate public key");
            var hash = SHA256.HashData("owned synthetic certificate proof"u8);
            var signature = key.SignHash(hash);
            if (certificate.HasPrivateKey != (mode == "legacy_private_certificate") ||
                !verify.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey) || !verify.VerifyHash(hash, signature))
                throw new IOException("Certificate identity ownership proof failed");
            GC.KeepAlive(certificate.RawData);
            verifyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        finally
        {
            start = Stopwatch.GetTimestamp(); certificate?.Dispose(); key.Dispose();
            disposeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return new(keyMs, requestMs, certificateMs, verifyMs, disposeMs, GC.GetAllocatedBytesForCurrentThread()-allocated);
    }
    internal static async Task Retention()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Console.WriteLine("mode,wave,operations,managed_heap,managed_committed,fragmented_bytes,rss,private_bytes,handles,cpu_ms");
        var modes = new[] { "empty_control", "legacy_private_certificate", "public_only_certificate", "runtime_identity" };
        var selected = Environment.GetEnvironmentVariable("TRYAGI_NATIVE_MODE");
        if (selected is not null && !modes.Contains(selected)) throw new ArgumentException("Unknown native control mode");
        if (selected is not null) modes = [selected];
        Console.WriteLine($"# isolation={(selected is null ? "interleaved" : "single_mode_process")}; warmup=32; tiered_override={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}");
        foreach (var mode in modes) for (var i = 0; i < 32; i++) { deadline.Token.ThrowIfCancellationRequested(); if (mode != "empty_control") Generate(mode); }
        // Separate process mode is used by CI; interleaving is available for local controls.
        for (var wave = 0; wave < 8; wave++)
        foreach (var mode in modes)
        {
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
            for (var i = 0; i < 16; i++) { deadline.Token.ThrowIfCancellationRequested(); if (mode != "empty_control") Generate(mode); }
            GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
            await Task.Delay(50, deadline.Token);
            var heap = GC.GetTotalMemory(true); var info = GC.GetGCMemoryInfo(); process.Refresh();
            Console.WriteLine($"{mode},{wave},16,{heap},{info.TotalCommittedBytes},{info.FragmentedBytes},{process.WorkingSet64},{(process.PrivateMemorySize64>0?process.PrivateMemorySize64.ToString():"unavailable")},{(OperatingSystem.IsMacOS()?"unavailable":process.HandleCount.ToString())},{(process.TotalProcessorTime-cpu).TotalMilliseconds:F3}");
        }
    }
}
