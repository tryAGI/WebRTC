using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using tryAGI.WebRTC;

internal static class MediaBenchmarks
{
    internal static async Task Run()
    {
        Console.WriteLine("profile,payload_bytes,trace,caller_token,sample,packets,process_managed_bytes_per_packet,ns_per_packet,p50_ns,p95_ns,p99_ns,gen0,gen1,gen2");
        foreach (var profile in Enum.GetValues<SrtpProfile>())
        foreach (var payload in new[] { 60, 1000 })
        foreach (var trace in new[] { false, true })
        foreach (var cancellable in new[] { false, true })
            await Scenario(profile, payload, trace, cancellable);
        // Whole-process observations after bounded disposal, not pure native usage or leak proof.
        var heap = GC.GetTotalMemory(true);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"# after_all_peers_disposed_managed_heap={heap}; process_working_set={process.WorkingSet64}; process_private_bytes={(process.PrivateMemorySize64 > 0 ? process.PrivateMemorySize64.ToString() : "unavailable")}");
    }
    private static async Task Scenario(SrtpProfile profile, int payload, bool trace, bool cancellable)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        using var capture = trace ? left.AttachDiagnostics(new() { Metrics = false, PacketTrace = true, EventCapacity = 512 }) : null;
        using var receiveCapture = trace ? right.AttachDiagnostics(new() { Metrics = false, PacketTrace = true, EventCapacity = 512 }) : null;
        var options = new DtlsSrtpOptions { Profiles = [profile] };
        await using var sender = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256(), options);
        await using var receiver = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), options);
        await Task.WhenAll(sender.ConnectAsync(ct), receiver.ConnectAsync(ct));
        await using var reader = receiver.ReceiveMediaDatagramsAsync(ct).GetAsyncEnumerator(ct);
        var packet = new byte[payload + 12]; packet[0] = 0x80; packet[1] = 111;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 42);
        var latencies = new long[512];
        var sequence = 0;
        var caller = cancellable ? ct : CancellationToken.None;
        await Exercise(128, false);
        for (var sample = 0; sample < 5; sample++)
        {
            var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
            var allocated = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            await Exercise(latencies.Length, true);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            gen0 = GC.CollectionCount(0) - gen0; gen1 = GC.CollectionCount(1) - gen1; gen2 = GC.CollectionCount(2) - gen2;
            Array.Sort(latencies);
            double Ns(int index) => latencies[index] * 1e9 / Stopwatch.Frequency;
            Console.WriteLine($"{profile},{payload},{trace},{cancellable},{sample},{latencies.Length},{bytes / (double)latencies.Length:F2},{elapsed.TotalNanoseconds / latencies.Length:F1},{Ns(255):F1},{Ns(486):F1},{Ns(506):F1},{gen0},{gen1},{gen2}");
        }
        if (sender.GetDiagnostics().RejectedRecords != 0 || receiver.GetDiagnostics().RejectedRecords != 0 ||
            receiver.GetDiagnostics().DroppedMediaDatagrams != 0)
            throw new IOException("Media benchmark experienced rejection or queue drops");
        async Task Exercise(int count, bool measure)
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)sequence++));
                var start = Stopwatch.GetTimestamp();
                await sender.SendRtpAsync(packet, caller);
                if (!await reader.MoveNextAsync() || reader.Current.Kind != SecureMediaKind.Rtp ||
                    !reader.Current.Data.AsSpan().SequenceEqual(packet))
                    throw new IOException("Media benchmark authenticated receive mismatch");
                if (measure) latencies[i] = Stopwatch.GetTimestamp() - start;
            }
        }
    }
}
