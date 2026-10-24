using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

// Synthetic loopback only. Corruption happens after encryption, preserving the
// production sender's nonce/sequence discipline. Report process allocations,
// including this proxy/harness, rather than claiming an isolated crypto budget.
internal static class AuthenticationLoadBenchmarks
{
    internal static async Task Run(bool control)
    {
        var pacing = Environment.GetEnvironmentVariable("TRYAGI_AUTH_LOAD_PACING") ?? "interval";
        if (pacing is not ("interval" or "deadline")) throw new ArgumentException("Unknown authentication-load pacing");
        Console.WriteLine($"# auth_load_pacing={pacing}");
        Console.WriteLine("profile,burst,sample,offered,received,missing,injected,rejected,ice_drops,media_drops,fixture_drops,process_bytes,cpu_ms,elapsed_ms,p50_ns,p95_ns,p99_ns,gen0,gen1,gen2,min_send_gap_us,max_send_gap_us,max_injection_us");
        foreach (var profile in control ? new[] { SrtpProfile.AeadAes128Gcm } : Enum.GetValues<SrtpProfile>())
        foreach (var burst in control ? new[] { 0 } : new[] { 0, 16, 64, 256 })
        {
            try { await Scenario(profile, burst, control, pacing); }
            catch (OperationCanceledException error) { Fail("deadline", error, profile, burst, pacing); return; }
            catch (IOException error) { Fail("integrity", error, profile, burst, pacing); return; }
        }
    }

    private static void Fail(string reason, Exception error, SrtpProfile profile, int burst, string pacing)
    {
        // Keep failure visible to both CI and offline CSV consumers. Expected
        // workload failures exit1 rather than producing an unhandled abort/core.
        Console.WriteLine($"# auth_load_failure={reason};profile={profile};burst={burst};pacing={pacing}");
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }

    private static async Task Scenario(SrtpProfile profile, int burst, bool control, string pacing)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var proxy = new CorruptingProxy(left.LocalEndPoint, right.LocalEndPoint, burst, control);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(proxy.EndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(proxy.EndPoint)], ct));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        var options = new DtlsSrtpOptions { Profiles = [profile] };
        await using var sender = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256(), options);
        await using var receiver = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), options);
        await Task.WhenAll(sender.ConnectAsync(ct), receiver.ConnectAsync(ct));
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sent = new long[316]; var latencies = new long[316];
        var collecting = Collect(); var packet = new byte[72]; packet[0] = 0x80; packet[1] = 111; packet[11] = 42;
        using var process = Process.GetCurrentProcess();
        try
        {
            for (var i = 0; i < 16; i++) await Send(i);
            await Drain(15);
            if (latencies.AsSpan(0, 16).Contains(0L)) throw new IOException("Authentication load warm-up lost valid traffic");
            for (var sample = 0; sample < 3; sample++)
            {
                var first = 16 + sample * 100;
                var rejects = receiver.GetDiagnostics().RejectedRecords; var iceDrops = right.GetDiagnostics().DroppedDatagrams;
                var mediaDrops = receiver.GetDiagnostics().DroppedMediaDatagrams;
                var injected = proxy.Injected; var dropped = proxy.Dropped;
                process.Refresh(); var cpu = process.TotalProcessorTime;
                var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
                var bytes = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < 100; i++)
                {
                    if (pacing == "interval" && i > 0)
                    {
                        while (true)
                        {
                            var remaining = TimeSpan.FromMilliseconds(20) - Stopwatch.GetElapsedTime(Volatile.Read(ref sent[first + i - 1]));
                            if (remaining <= TimeSpan.Zero) break;
                            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))), ct);
                        }
                    }
                    await Send(first + i);
                    if (pacing == "deadline")
                    {
                        var remaining = TimeSpan.FromMilliseconds((i + 1) * 20) - Stopwatch.GetElapsedTime(start);
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
                    }
                }
                await Drain(first + 99);
                var elapsed = Stopwatch.GetElapsedTime(start); var allocated = GC.GetTotalAllocatedBytes(true) - bytes;
                g0 = GC.CollectionCount(0) - g0; g1 = GC.CollectionCount(1) - g1; g2 = GC.CollectionCount(2) - g2;
                process.Refresh(); var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var values = latencies.Skip(first).Take(100).Where(value => value > 0).Order().ToArray();
                if (values.Length == 0) throw new IOException("Authentication stress starved all accepted traffic");
                double Percentile(double p) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * p) - 1)] * 1e9 / Stopwatch.Frequency;
                var gaps = new long[99];
                for (var i = 1; i < 100; i++) gaps[i - 1] = Volatile.Read(ref sent[first + i]) - Volatile.Read(ref sent[first + i - 1]);
                var minGap = gaps.Min() * 1e6 / Stopwatch.Frequency; var maxGap = gaps.Max() * 1e6 / Stopwatch.Frequency;
                var injection = proxy.MaximumInjectionMicroseconds(first, 100);
                Console.WriteLine($"{profile},{burst},{sample},100,{values.Length},{100 - values.Length},{proxy.Injected - injected},{receiver.GetDiagnostics().RejectedRecords - rejects},{right.GetDiagnostics().DroppedDatagrams - iceDrops},{receiver.GetDiagnostics().DroppedMediaDatagrams - mediaDrops},{proxy.Dropped - dropped},{allocated},{cpuMs:F3},{elapsed.TotalMilliseconds:F3},{Percentile(.5):F1},{Percentile(.95):F1},{Percentile(.99):F1},{g0},{g1},{g2},{minGap:F3},{maxGap:F3},{injection:F3}");
            }
        }
        finally { reading.Cancel(); await collecting; }

        async Task Send(int sequence)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)sequence));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)sequence * 960);
            Volatile.Write(ref sent[sequence], Stopwatch.GetTimestamp());
            await sender.SendRtpAsync(packet, ct);
        }
        async Task Drain(int sequence)
        {
            while (proxy.Processed < sequence) { ct.ThrowIfCancellationRequested(); await Task.Delay(10, ct); }
            await Task.Delay(250, ct);
        }
        async Task Collect()
        {
            try
            {
                await foreach (var item in receiver.ReceiveMediaDatagramsAsync(reading.Token))
                {
                    var bytes = item.Data;
                    if (item.Kind != SecureMediaKind.Rtp || bytes.Length != 72) throw new IOException("Unexpected authenticated stress payload");
                    var sequence = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));
                    if (sequence >= sent.Length || bytes[0] != 0x80 || bytes[1] != 111 ||
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)) != (uint)sequence * 960 ||
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)) != 42 || bytes.AsSpan(12).ContainsAnyExcept((byte)0))
                        throw new IOException("Corrupted authentication packet reached the accepted queue");
                    var began = Volatile.Read(ref sent[sequence]);
                    if (began == 0 || Interlocked.CompareExchange(ref latencies[sequence], Math.Max(1, Stopwatch.GetTimestamp() - began), 0) != 0)
                        throw new IOException("Duplicate or unsolicited authenticated stress packet");
                }
            }
            catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        }
    }
}

internal sealed class CorruptingProxy : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _processed = -1;
    private long _injected, _dropped;
    private readonly long[] _injectionTicks = new long[316];
    internal double MaximumInjectionMicroseconds(int first, int count)
    {
        long maximum = 0;
        for (var i = first; i < first + count; i++) maximum = Math.Max(maximum, Volatile.Read(ref _injectionTicks[i]));
        return maximum * 1e6 / Stopwatch.Frequency;
    }
    internal int Processed => Volatile.Read(ref _processed);
    internal long Injected => Interlocked.Read(ref _injected);
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal IPEndPoint EndPoint { get; }
    internal CorruptingProxy(IPEndPoint left, IPEndPoint right, int burst, bool control)
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        EndPoint = _socket.LocalEndPoint as IPEndPoint ?? throw new IOException("Proxy endpoint missing");
        _worker = Forward();
        async Task Forward()
        {
            var buffer = new byte[4096]; var corrupt = new byte[4096];
            try
            {
                while (true)
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                    var fromLeft = result.RemoteEndPoint.Equals(left);
                    if (!fromLeft && !result.RemoteEndPoint.Equals(right)) throw new IOException("Unexpected proxy source");
                    var length = result.ReceivedBytes; var sequence = -1;
                    if (fromLeft && length >= 12 && buffer[0] >> 6 == 2 && (buffer[1] & 127) == 111)
                    {
                        sequence = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
                        if (sequence >= 16)
                        {
                            buffer.AsSpan(0, length).CopyTo(corrupt);
                            // Mutate both authenticated header and tag. An admitted bad
                            // packet cannot masquerade as its following valid original.
                            corrupt[4] ^= 0x40; corrupt[length - 1] ^= 1;
                            var injectionStart = Stopwatch.GetTimestamp();
                            for (var i = 0; i < burst; i++)
                            {
                                await _socket.SendToAsync(corrupt.AsMemory(0, length), SocketFlags.None, right, _lifetime.Token);
                                Interlocked.Increment(ref _injected);
                            }
                            if (sequence >= _injectionTicks.Length) throw new IOException("Authentication fixture sequence exceeds bound");
                            Volatile.Write(ref _injectionTicks[sequence], Stopwatch.GetTimestamp() - injectionStart);
                        }
                    }
                    if (control && sequence == 16) Interlocked.Increment(ref _dropped);
                    else await _socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, fromLeft ? right : left, _lifetime.Token);
                    if (sequence >= 0) Volatile.Write(ref _processed, sequence);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _worker; }
        finally { _socket.Dispose(); _lifetime.Dispose(); }
    }
}
