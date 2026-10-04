using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class DiagnosticBoundaryTests
{
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    internal static async Task SecureProcessingWait()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = timeout.Token;
        await using var a = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var b = new IceUdpTransport(new(IPAddress.Loopback, 0));
        using var capture = b.AttachDiagnostics(new() { PacketTrace = true });
        await Task.WhenAll(a.ConnectAsync(b.LocalCredentials, IceRole.Controlling, [new(b.LocalEndPoint)], ct),
            b.ConnectAsync(a.LocalCredentials, IceRole.Controlled, [new(a.LocalEndPoint)], ct));
        await a.SendDatagramAsync(new byte[] { 0x80, 1, 2 }, ct);
        // Hold the upper receive consumer AFTER managed socket handling has completed.
        var until = Stopwatch.StartNew();
        while (capture.GetSnapshot().RecordedEvents < 8 && until.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(1, ct);
        await Task.Delay(80, ct);
        await foreach (var packet in b.ReceiveDatagramsAsync(ct)) { Check(packet[2] == 2, "Wrong delayed datagram"); break; }
        var events = new PacketStageEvent[capture.TraceCapacity]; var count = capture.Drain(events);
        var dequeued = events[..count].Last(e => e.Stage == PacketStage.IceDequeued);
        Check(dequeued.DurationTicks > Stopwatch.Frequency / 20, "Secure-processing wait was not attributed to ICE queue");
        var handler = events[..count].FirstOrDefault(e => e.PacketId == dequeued.PacketId && e.Stage == PacketStage.ReceiveHandlerCompleted);
        Check(handler.Stage == PacketStage.ReceiveHandlerCompleted && handler.DurationTicks < dequeued.DurationTicks, "Queue wait was mislabeled receive processing");
        Check(!capture.Clock.KernelReceiveTimestampSupported, "Fabricated kernel timing");
    }
    internal static async Task ManagedHandlerStall()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = timeout.Token;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var armed = 0;
        var fast = new IceUdpTransportOptions { ConsentInterval = TimeSpan.FromMilliseconds(100), ConsentTimeout = TimeSpan.FromSeconds(2) };
        await using var a = new IceUdpTransport(new(IPAddress.Loopback, 0), options: fast);
        await using var b = new IceUdpTransport(new(IPAddress.Loopback, 0), options: fast with
        {
            // Controlled fault in the existing destination-policy boundary, never a diagnostics callback.
            RemoteCandidateFilter = candidate =>
            {
                if (Interlocked.CompareExchange(ref armed, 2, 1) == 1)
                { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(2)); }
                return true;
            },
        });
        using var capture = b.AttachDiagnostics(new() { PacketTrace = true, EventCapacity = 4096 });
        await Task.WhenAll(a.ConnectAsync(b.LocalCredentials, IceRole.Controlling, [new(b.LocalEndPoint)], ct),
            b.ConnectAsync(a.LocalCredentials, IceRole.Controlled, [new(a.LocalEndPoint)], ct));
        Volatile.Write(ref armed, 1);
        // Pure consent deliberately bypasses candidate admission; inject a fresh authenticated connectivity check.
        var check = new byte[1024];
        var writer = new StunMessageWriter(check, 1, System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        Check(writer.TryAddAttribute(6, System.Text.Encoding.ASCII.GetBytes(b.LocalCredentials.UsernameFragment + ":" + a.LocalCredentials.UsernameFragment)), "STUN username");
        Check(writer.TryAddUInt32(0x24, 1862270975) && writer.TryAddUInt64(0x802A, 42), "STUN role");
        Check(writer.TryComplete(System.Text.Encoding.ASCII.GetBytes(b.LocalCredentials.Password), true, out var length), "STUN integrity");
        await a.SendDatagramAsync(check.AsMemory(0, length), ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            await a.SendDatagramAsync(new byte[] { 0x80, 4, 5 }, ct);
            await Task.Delay(80, ct);
        }
        finally { release.Set(); }
        await foreach (var packet in b.ReceiveDatagramsAsync(ct)) { Check(packet[2] == 5, "Wrong queued datagram"); break; }
        var events = new PacketStageEvent[capture.TraceCapacity]; var count = capture.Drain(events);
        Check(events[..count].Any(e => e.Stage == PacketStage.ReceiveHandlerCompleted && e.DurationTicks > Stopwatch.Frequency / 20),
            "Controlled handler stall was not attributed to managed receive processing");
        Check(!capture.Clock.KernelReceiveTimestampSupported, "Buffered socket arrival time was fabricated");
    }
    internal static async Task NetworkFaults()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = timeout.Token;
        var options = new PeerConnectionOptions { LocalEndPoint = new(IPAddress.Loopback, 0), DataChannels = false, AudioQueueCapacity = 32 };
        await using var a = new PeerConnection(options); await using var b = new PeerConnection(options);
        var offer = a.CreateOffer(); var aEndpoint = SdpSessionDescription.Parse(offer).Media[0].Candidates[0].GetResolvedUdpCandidate()!.EndPoint;
        await using var proxy = new DatagramFaultProxy(aEndpoint);
        var answer = b.CreateAnswer(ReplaceCandidate(offer, aEndpoint, proxy.EndPoint));
        var bEndpoint = SdpSessionDescription.Parse(answer).Media[0].Candidates[0].GetResolvedUdpCandidate()!.EndPoint;
        proxy.Right = bEndpoint; a.SetRemoteAnswer(ReplaceCandidate(answer, bEndpoint, proxy.EndPoint));
        using var capture = b.AttachDiagnostics(new() { PacketTrace = true, IncludePacketIdentifiers = true, EventCapacity = 4096 });
        using var outgoing = a.AttachDiagnostics(new() { PacketTrace = true, EventCapacity = 4096 });
        await Task.WhenAll(a.ConnectAsync(ct), b.ConnectAsync(ct)); var payload = new byte[] { 0xf8, 0xff, 0xfe };
        async Task<EncodedOpusPacket> First() { await foreach (var packet in b.ReceiveAudioAsync(ct)) return packet; throw new IOException(); }
        for (uint n = 0; n < 3; n++) { await a.SendOpusAsync(payload, n * 960, cancellationToken: ct); await First(); }
        var baseline = b.GetRtpEvidence(true).ReceivedStreams.Single();
        proxy.Next = "delay"; var submitted = Stopwatch.GetTimestamp(); await a.SendOpusAsync(payload, 2880, cancellationToken: ct); await proxy.Acknowledged(ct); await First();
        Check(Stopwatch.GetElapsedTime(submitted) > TimeSpan.FromMilliseconds(50), "Delay fixture did not run");
        proxy.Next = "reorder"; await a.SendOpusAsync(payload, 3840, cancellationToken: ct); await proxy.Acknowledged(ct); await a.SendOpusAsync(payload, 4800, cancellationToken: ct);
        var high = await First(); var low = await First(); Check(high.Timestamp == 4800 && low.Timestamp == 3840, "Reorder fixture did not run");
        proxy.Next = "duplicate"; await a.SendOpusAsync(payload, 5760, cancellationToken: ct); await proxy.Acknowledged(ct); await First();
        proxy.Next = "corrupt"; await a.SendOpusAsync(payload, 6720, cancellationToken: ct); await proxy.Acknowledged(ct);
        proxy.Next = "drop"; await a.SendOpusAsync(payload, 7680, cancellationToken: ct); await proxy.Acknowledged(ct);
        // Await fixture acknowledgements before changing its next action (one source owner, no timing guess).
        await a.SendOpusAsync(payload, 8640, cancellationToken: ct); await First();
        var evidence = b.GetRtpEvidence(true).ReceivedStreams.Single();
        Check(evidence.ResetEpoch == baseline.ResetEpoch && evidence.CumulativeLost == 2, "Loss/reorder state inconsistent");
        var events = new PacketStageEvent[capture.TraceCapacity]; var count = capture.Drain(events); var observed = events[..count];
        void RequireObserved(PacketStage stage, PacketReason reason)
        {
            Check(capture.GetEventCounts().Any(e => e.Stage == stage && e.Reason == reason && e.Count > 0), "Missing exact stage counter: " + reason);
            if (!observed.Any(e => e.Stage == stage && e.Reason == reason))
                Check(capture.GetSnapshot().TraceEventsDropped > 0, "Missing trace record without an explicit capture-drop signal");
        }
        RequireObserved(PacketStage.Dropped, PacketReason.ReplayOrTooOld);
        RequireObserved(PacketStage.Dropped, PacketReason.Authentication);
        RequireObserved(PacketStage.AudioEnqueued, PacketReason.Reordered);
        var completed = observed.Where(e => e.Stage == PacketStage.ManagedReceiveCompleted).ToArray();
        Check(completed.Zip(completed.Skip(1)).Any(p => p.Second.TimestampTicks - p.First.TimestampTicks > Stopwatch.Frequency / 20), "Pre-managed receive gap was not observable");
        // Processing after the delayed receive must not be labeled as the injected network delay.
        var delayed = observed.Single(e => e.Stage == PacketStage.ConsumerDelivery && e.RtpTimestamp == 2880);
        Check(observed.Where(e => e.PacketId == delayed.PacketId && e.Stage is PacketStage.AuthenticationStarted or PacketStage.Authenticated)
            .All(e => e.DurationTicks < Stopwatch.Frequency / 20), "Network delay leaked into authentication timing");
        Check(!capture.Clock.KernelReceiveTimestampSupported, "Unsupported OS attribution must stay unknown");
    }
    private static string ReplaceCandidate(string sdp, IPEndPoint original, IPEndPoint replacement) => sdp.Replace(
        $" {original.Address} {original.Port} typ host", $" {replacement.Address} {replacement.Port} typ host", StringComparison.Ordinal);
}

internal sealed class DatagramFaultProxy : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IPEndPoint _left;
    private readonly Task _reader;
    private readonly SemaphoreSlim _ack = new(0);
    private string? _next;
    private byte[]? _held;
    public IPEndPoint? Right;
    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public string Next { set { Volatile.Write(ref _next, value); } }
    public DatagramFaultProxy(IPEndPoint left)
    { _left = left; _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _reader = Run(); }
    private async Task Run()
    {
        var buffer = new byte[2048];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                var fromLeft = received.RemoteEndPoint.Equals(_left); var destination = fromLeft ? Right : _left;
                if (destination == null || !fromLeft && !received.RemoteEndPoint.Equals(Right)) continue;
                var data = buffer[..received.ReceivedBytes];
                if (fromLeft && data.Length >= 2 && data[0] is >= 128 and <= 191 && data[1] is not (>= 192 and <= 223))
                {
                    var action = Interlocked.Exchange(ref _next, null);
                    if (action == "delay") await Task.Delay(80, _lifetime.Token);
                    if (action == "reorder") { _held = data; _ack.Release(); continue; }
                    if (action == "drop") { _ack.Release(); continue; }
                    if (action == "corrupt") data[^1] ^= 1;
                    await _socket.SendToAsync(data, SocketFlags.None, destination, _lifetime.Token);
                    if (action == "duplicate") await _socket.SendToAsync(data, SocketFlags.None, destination, _lifetime.Token);
                    if (_held != null) { await _socket.SendToAsync(_held, SocketFlags.None, destination, _lifetime.Token); _held = null; }
                    if (action != null) _ack.Release();
                }
                else await _socket.SendToAsync(data, SocketFlags.None, destination, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public Task Acknowledged(CancellationToken ct) => _ack.WaitAsync(ct);
    public async ValueTask DisposeAsync()
    { _lifetime.Cancel(); await _reader; _socket.Dispose(); _lifetime.Dispose(); _ack.Dispose(); }
}
