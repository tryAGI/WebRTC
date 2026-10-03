using System.Buffers.Binary;
using System.Net;
using System.Security.Authentication;
using tryAGI.WebRTC;

internal static class TurnStreamTests
{
    private static void Check(bool value, string text = "TURN stream assertion failed") => TurnFixture.Check(value, text);
    internal static async Task RoundTrip(bool tls, bool channel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture(tls) { Fragment = true };
        await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server,
            new(TurnFixture.Username, TurnFixture.Secret), server.Options, ct);
        if (channel) await allocation.BindChannelAsync(server.Backend.Peer, ct); else await allocation.CreatePermissionAsync(server.Backend.Peer, ct);
        foreach (var size in new[] { 0, 1, 2, 3, 4, 117 })
        {
            var payload = Enumerable.Range(0, size).Select(n => (byte)n).ToArray();
            await allocation.SendDatagramAsync(server.Backend.Peer, payload, ct);
            await foreach (var packet in allocation.ReceiveDatagramsAsync(ct)) { Check(packet.Data.AsSpan().SequenceEqual(payload) && packet.Source.Equals(server.Backend.Peer)); break; }
        }
        await allocation.RefreshAsync(ct);
        Check(allocation.GetDiagnostics() is { Retransmissions: 0, ModernIntegrity: true, ReceivedDatagrams: 6 });
        await allocation.DisposeAsync(); Check(server.Backend.Allocations == 0 && allocation.GetDiagnostics().GracefulReleaseAcknowledged);
    }
    internal static async Task TlsReject(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture(true, expired: scenario == "expired", wrongPurpose: scenario == "purpose") { AllowTlsRejection = true };
        var options = server.Options;
        if (scenario == "name") options = options with { Tls = options.Tls! with { ServerName = "wrong.fixture.local" } };
        if (scenario == "chain") options = options with { Tls = options.Tls! with { TrustedRootCertificates = [] } };
        try
        {
            await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server,
                new(TurnFixture.Username, TurnFixture.Secret), options, ct);
            throw new InvalidOperationException("Untrusted TLS server accepted");
        }
        catch (AuthenticationException) { }
        Check(server.Backend.Requests == 0, "TURN credentials were sent before TLS identity validation");
    }
    internal static async Task HandshakeCancellation(bool timeout)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new TurnStreamFixture(true) { StallHandshake = true };
        using var caller = new CancellationTokenSource();
        var options = server.Options with { ConnectTimeout = TimeSpan.FromMilliseconds(timeout ? 100 : 2000) };
        var task = TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server, new(TurnFixture.Username, TurnFixture.Secret), options, caller.Token);
        if (!timeout) { await Task.Delay(100, deadline.Token); caller.Cancel(); }
        try { await using var allocation = await task.WaitAsync(deadline.Token); throw new InvalidOperationException("Stalled handshake accepted"); }
        catch (TimeoutException) when (timeout) { }
        catch (OperationCanceledException) when (!timeout && caller.IsCancellationRequested) { }
        Check(server.Backend.Requests == 0);
    }
    internal static async Task Malformed(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture();
        await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server, new(TurnFixture.Username, TurnFixture.Secret), server.Options, ct);
        byte[] bad = scenario switch
        {
            "prefix" => [0x80, 0, 0, 0],
            "channel" => [0x40, 0, 0xFF, 0xFF],
            "control" => [1, 3, 0xFF, 0xFC],
            "length" => [1, 3, 0, 1],
            "cookie" => new byte[20],
            "header" => [0, 0, 0],
            "body" => [0x40, 0, 0, 2, 0],
            "padding" => [0x40, 0, 0, 1, 0],
            _ => throw new ArgumentException(scenario),
        };
        await server.Inject(bad, ct);
        if (scenario is "header" or "body" or "padding") server.EndStream();
        var failure = await allocation.Completion.WaitAsync(ct);
        Check((scenario is "header" or "body" or "padding" ? failure is EndOfStreamException : failure is InvalidDataException) &&
            !allocation.GetDiagnostics().AllocationActive, $"Unexpected framing failure: {failure?.GetType().Name}");
    }
    internal static async Task Coalesced()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture();
        await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server, new(TurnFixture.Username, TurnFixture.Secret), server.Options, ct);
        var number = await allocation.BindChannelAsync(server.Backend.Peer, ct);
        var combined = new byte[24];
        for (var n = 0; n < 3; n++)
        { BinaryPrimitives.WriteUInt16BigEndian(combined.AsSpan(n * 8), number); combined[n * 8 + 3] = (byte)(n + 1); combined.AsSpan(n * 8 + 4, n + 1).Fill((byte)n); }
        await server.Inject(combined, ct);
        var read = 0;
        await foreach (var packet in allocation.ReceiveDatagramsAsync(ct))
        { Check(packet.Data.Length == read + 1 && packet.Data.All(b => b == read)); if (++read == 3) break; }
    }
    internal static async Task NoRetransmit()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture();
        await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server, new(TurnFixture.Username, TurnFixture.Secret), server.Options, ct);
        server.Backend.HoldRefresh = true;
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        try { await allocation.RefreshAsync(caller.Token); throw new InvalidOperationException("Silent server acknowledged refresh"); } catch (OperationCanceledException) { }
        Check(server.Backend.Requests == 3 && allocation.GetDiagnostics().Retransmissions == 0 && !allocation.Completion.IsCompleted);
        server.Backend.HoldRefresh = false; await allocation.RefreshAsync(ct);
    }
    internal static async Task BlockedWrite(bool tls, bool timeout = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture(tls);
        await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server,
            new(TurnFixture.Username, TurnFixture.Secret), server.Options with { MaximumDatagramSize = 16384, StreamWriteTimeout = TimeSpan.FromMilliseconds(timeout ? 500 : 5000) }, ct);
        await allocation.BindChannelAsync(server.Backend.Peer, ct);
        // The bridge may finish the one header read already in progress; then it stops consuming the stream.
        server.HoldReads = true;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await allocation.SendDatagramAsync(server.Backend.Peer, new byte[1], canceled.Token); throw new InvalidOperationException("Pre-canceled send accepted"); }
        catch (OperationCanceledException) { }
        Check(!allocation.Completion.IsCompleted, "Cancellation before write admission destroyed the owner");
        using var blocked = new CancellationTokenSource(timeout ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(500));
        var bytes = new byte[16384]; var canceledWrite = false;
        try
        {
            for (var n = 0; n < 4096; n++) await allocation.SendDatagramAsync(server.Backend.Peer, bytes, blocked.Token);
        }
        catch (TimeoutException) when (timeout) { canceledWrite = true; }
        catch (Exception error) when (!timeout && blocked.IsCancellationRequested && error is OperationCanceledException or IOException or System.Net.Sockets.SocketException)
        { canceledWrite = true; }
        Check(canceledWrite, "Fixture never induced stream write backpressure");
        Check(await allocation.Completion.WaitAsync(ct) is Exception && !allocation.GetDiagnostics().AllocationActive,
            "An interrupted stream write left its allocation reusable");
        await Task.WhenAll(allocation.DisposeAsync().AsTask(), allocation.DisposeAsync().AsTask()).WaitAsync(ct);
        Check(!allocation.GetDiagnostics().GracefulReleaseAcknowledged);
    }
    internal static async Task WriterDisposal(bool tls)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture(tls);
        await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server,
            new(TurnFixture.Username, TurnFixture.Secret), server.Options with { MaximumDatagramSize = 16384 }, ct);
        await allocation.BindChannelAsync(server.Backend.Peer, ct); server.HoldReads = true;
        var bytes = new byte[16384];
        var filling = Task.Run(async () =>
        {
            for (var n = 0; n < 4096; n++) await allocation.SendDatagramAsync(server.Backend.Peer, bytes, ct);
            throw new InvalidOperationException("Fixture did not induce write backpressure");
        });
        await server.ReadPaused.Task.WaitAsync(ct); await Task.Delay(100, ct);
        Check(!filling.IsCompleted, "Write backpressure was lost before queue admission");
        var queued = Enumerable.Range(0, 63).Select(_ => allocation.SendDatagramAsync(server.Backend.Peer, bytes, ct).AsTask()).ToArray();
        try { await allocation.SendDatagramAsync(server.Backend.Peer, bytes, ct); throw new IOException("Stream writer budget exceeded"); }
        catch (InvalidOperationException) { }
        // Saturation is visible to callers; the separate control reservation remains available to disposal.
        await Task.WhenAll(allocation.DisposeAsync().AsTask(), allocation.DisposeAsync().AsTask()).WaitAsync(ct);
        foreach (var writing in queued.Append(filling))
        {
            try { await writing.WaitAsync(ct); }
            catch (Exception error) when (!ct.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
        }
        Check(allocation.Completion.IsCompleted && !allocation.GetDiagnostics().GracefulReleaseAcknowledged);
    }
    internal static async Task IceFailure(bool tls, bool selected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture(tls);
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new() { RelayOnly = selected });
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        var relay = await left.GatherRelayCandidateAsync(server.Server, new(TurnFixture.Username, TurnFixture.Secret), server.Options, ct);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [selected ? relay : new(left.LocalEndPoint)], ct));
        Check(left.GetDiagnostics().SelectedLocalCandidateType == (selected ? IceCandidateType.Relay : IceCandidateType.Host));
        server.EndStream();
        if (selected)
        {
            Check(await left.Completion.WaitAsync(ct) is IOException && !left.IsConnected, "Selected stream failure retained a connected ICE path");
            try { await left.SendDatagramAsync("\u0016failed"u8.ToArray(), ct); throw new InvalidOperationException("Failed selected relay still sent traffic"); }
            catch (IOException) { }
        }
        else
        {
            while (left.GetDiagnostics().LocalPaths != 1) await Task.Delay(5, ct);
            Check(left.IsConnected && !left.Completion.IsCompleted, "Unselected stream failure killed the host path");
            await left.SendDatagramAsync("\u0016surviving-host"u8.ToArray(), ct);
            await foreach (var bytes in right.ReceiveDatagramsAsync(ct)) { Check(bytes.AsSpan().SequenceEqual("\u0016surviving-host"u8)); break; }
        }
    }
    internal static async Task ConcurrentWrites(bool tls)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var server = new TurnStreamFixture(tls);
        await using var allocation = await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback, 0), server.Server, new(TurnFixture.Username, TurnFixture.Secret), server.Options, ct);
        await allocation.BindChannelAsync(server.Backend.Peer, ct);
        var sending = Enumerable.Range(0, 32).Select(n => allocation.SendDatagramAsync(server.Backend.Peer, Enumerable.Repeat((byte)n, 513).ToArray(), ct).AsTask()).ToList();
        sending.Add(allocation.RefreshAsync(ct)); await Task.WhenAll(sending);
        var seen = new HashSet<byte>();
        await foreach (var packet in allocation.ReceiveDatagramsAsync(ct))
        { Check(packet.Data.Length == 513 && packet.Data.All(b => b == packet.Data[0]) && seen.Add(packet.Data[0])); if (seen.Count == 32) break; }
        Check(server.FramesRead == 36 && allocation.GetDiagnostics().Retransmissions == 0);
    }
}
