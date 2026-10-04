using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using tryAGI.WebRTC;

internal static class IceServerTests
{
    private static void Check(bool value, string message = "ICE URI gathering assertion failed") => TurnFixture.Check(value, message);
    private static IceServerResolutionOptions Local(TimeSpan? timeout = null) => new()
    { EndpointFilter = e => IPAddress.IsLoopback(e.Address), GatherTimeout = timeout ?? TimeSpan.FromSeconds(4) };
    private static StunGatheringOptions Fast() => new()
    { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100), MaximumRequests = 2, FinalWaitMultiplier = 1, Timeout = TimeSpan.FromSeconds(2) };
    private static PeerConnection Peer(IPAddress? address = null) => new(new() { LocalEndPoint = new(address ?? IPAddress.Loopback, 0) });
    private static Socket Server(IPAddress address)
    { var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp); socket.Bind(new IPEndPoint(address, 0)); return socket; }
    private static async Task<IPEndPoint> Request(Socket server, CancellationToken ct, bool reply)
    {
        var bytes = new byte[128]; var any = server.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
        var read = await server.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(any, 0), ct);
        Check(StunMessage.TryParse(bytes.AsSpan(0, read.ReceivedBytes), out var message) && message.VerifyFingerprint());
        if (reply)
        {
            var output = new byte[128]; var writer = new StunMessageWriter(output, 0x0101, message.TransactionId);
            Check(writer.TryAddXorMappedAddress((IPEndPoint)read.RemoteEndPoint));
            Check(writer.TryComplete([], true, out var size));
            await server.SendToAsync(output.AsMemory(0, size), SocketFlags.None, read.RemoteEndPoint, ct);
        }
        return (IPEndPoint)read.RemoteEndPoint;
    }
    internal static async Task Stun(bool ipv6, bool dns)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = deadline.Token;
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var server = Server(address); await using var peer = Peer(address);
        var endpoint = (IPEndPoint)server.LocalEndPoint!;
        var host = dns ? "localhost" : ipv6 ? "[::1]" : "127.0.0.1";
        var uri = IceServerUri.Parse($"stun:{host}:{endpoint.Port}");
        var resolved = await uri.ResolveAsync(address.AddressFamily, Local(), ct);
        Check(resolved.Count is >= 1 and <= 8 && resolved.All(e => e.AddressFamily == address.AddressFamily && IPAddress.IsLoopback(e.Address)));
        var offer = peer.CreateOffer(); var responding = Request(server, ct, true);
        // Attempted predicate mutation cannot redirect the pinned endpoint.
        var policy = Local() with { EndpointFilter = e => { var allowed = e.Equals(endpoint); e.Port = 1; if (ipv6) e.Address.ScopeId = 12; return allowed; } };
        var candidate = await peer.GatherServerReflexiveCandidateAsync(uri, policy, Fast(), ct);
        Check(candidate.Type == IceCandidateType.ServerReflexive && candidate.EndPoint.Equals(await responding));
        Check(peer.GetGatheringDiagnostics().SentRequests == 1 && peer.LocalDescription == offer);
        peer.CompleteGathering(); Check(peer.LocalDescription!.Contains("a=end-of-candidates", StringComparison.Ordinal));
        try { await peer.GatherServerReflexiveCandidateAsync(uri, Local(), Fast(), ct); throw new InvalidOperationException("Completed gather accepted"); }
        catch (InvalidOperationException error) when (error.Message.Contains("Gathering is complete", StringComparison.Ordinal)) { }
    }
    internal static async Task Policy()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = deadline.Token;
        using var server = Server(IPAddress.Loopback); var endpoint = (IPEndPoint)server.LocalEndPoint!; await using var peer = Peer();
        var seen = 0;
        foreach (var host in new[] { "localhost", "127.0.0.1", "[::1]", "0.0.0.0", "224.0.0.1", "[ff02::1]", "[::]" })
        {
            var uri = IceServerUri.Parse($"stun:{host}:{endpoint.Port}");
            try { await peer.GatherServerReflexiveCandidateAsync(uri, Local() with { EndpointFilter = _ => { seen++; return false; } }, Fast(), ct); throw new InvalidOperationException("Forbidden server contacted"); }
            catch (IOException) { }
        }
        Check(seen >= 2 && server.Available == 0 && peer.GetGatheringDiagnostics().SentRequests == 0);
        foreach (var uri in new[] { "stuns:localhost", "turn:localhost", "turns:localhost?transport=udp" })
        {
            try
            {
                if (uri.StartsWith("turns", StringComparison.Ordinal)) await peer.GatherRelayCandidateAsync(IceServerUri.Parse(uri), new(TurnFixture.Username, TurnFixture.Secret), Local(), cancellationToken: ct);
                else await peer.GatherServerReflexiveCandidateAsync(IceServerUri.Parse(uri), Local(), cancellationToken: ct);
                throw new InvalidOperationException("Unsupported URI contacted");
            }
            catch (NotSupportedException) { }
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await IceServerUri.Parse("stun:localhost").ResolveAsync(AddressFamily.InterNetwork, Local(), canceled.Token); throw new InvalidOperationException("Canceled resolution accepted"); }
        catch (OperationCanceledException) { }
        foreach (var bad in new[] { Local() with { MaximumAddresses = 0 }, Local() with { MaximumAddresses = 17 }, Local() with { ResolveTimeout = TimeSpan.Zero }, Local() with { GatherTimeout = TimeSpan.Zero } })
        {
            try { await IceServerUri.Parse("stun:localhost").ResolveAsync(AddressFamily.InterNetwork, bad, ct); throw new InvalidOperationException("Invalid resolution bound accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        try { await IceServerUri.Parse("stun:localhost").ResolveAsync(AddressFamily.Unspecified, Local(), ct); throw new InvalidOperationException("Unbounded families accepted"); }
        catch (ArgumentOutOfRangeException) { }
        var responding = Request(server, ct, true);
        await peer.GatherServerReflexiveCandidateAsync(IceServerUri.Parse($"stun:localhost:{endpoint.Port}"), Local(), Fast(), ct);
        await responding; Check(peer.GetGatheringDiagnostics().SentRequests == 1);
    }
    internal static async Task Lifetime(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        using var server = Server(IPAddress.Loopback); await using var peer = Peer(); using var caller = new CancellationTokenSource();
        var uri = IceServerUri.Parse($"stun:localhost:{((IPEndPoint)server.LocalEndPoint!).Port}");
        var gathering = peer.GatherServerReflexiveCandidateAsync(uri, Local(scenario == "timeout" ? TimeSpan.FromMilliseconds(150) : null), Fast(), caller.Token);
        await Request(server, ct, false);
        try { peer.CompleteGathering(); throw new InvalidOperationException("Active gather completion accepted"); }
        catch (InvalidOperationException error) when (error.Message == "Gathering requests are still active.") { }
        if (scenario == "cancel") caller.Cancel();
        if (scenario == "dispose") await peer.DisposeAsync();
        try { await gathering.WaitAsync(ct); throw new InvalidOperationException("Silent gather succeeded"); }
        catch (TimeoutException) when (scenario == "timeout") { }
        catch (OperationCanceledException) when (scenario is "cancel" or "dispose") { }
        Check(peer.GetGatheringDiagnostics().ActiveTransactions == 0);
        if (scenario != "dispose") peer.CompleteGathering();
    }
    internal static async Task Admission()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var peer = Peer(); using var caller = new CancellationTokenSource();
        var servers = Enumerable.Range(0, 8).Select(_ => Server(IPAddress.Loopback)).ToArray();
        try
        {
            var gathers = servers.Select(server => peer.GatherServerReflexiveCandidateAsync(
                IceServerUri.Parse($"stun:localhost:{((IPEndPoint)server.LocalEndPoint!).Port}"), Local(), Fast() with { InitialRetransmissionTimeout = TimeSpan.FromSeconds(1) }, caller.Token)).ToArray();
            await Task.WhenAll(servers.Select(server => Request(server, ct, false)));
            Check(peer.GetGatheringDiagnostics().ActiveTransactions == 8);
            var denied = 0;
            try
            {
                await peer.GatherServerReflexiveCandidateAsync(IceServerUri.Parse("stun:localhost:1"), Local() with { EndpointFilter = _ => { denied++; return true; } }, Fast(), ct);
                throw new InvalidOperationException("Ninth URI gather admitted");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("budget is reserved", StringComparison.Ordinal)) { }
            Check(denied == 0, "Resolution/policy ran before candidate budget admission");
            caller.Cancel();
            foreach (var gather in gathers)
            { try { await gather.WaitAsync(ct); throw new InvalidOperationException("Canceled gather accepted"); } catch (OperationCanceledException) { } }
            Check(peer.GetGatheringDiagnostics().ActiveTransactions == 0);
            using var healthy = Server(IPAddress.Loopback); var responding = Request(healthy, ct, true);
            await peer.GatherServerReflexiveCandidateAsync(IceServerUri.Parse($"stun:localhost:{((IPEndPoint)healthy.LocalEndPoint!).Port}"), Local(), Fast(), ct);
            await responding; peer.CompleteGathering();
        }
        finally { foreach (var server in servers) server.Dispose(); }
    }
    internal static async Task Relay(TurnServerTransport transport, bool wrongName = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var peer = Peer();
        if (transport == TurnServerTransport.Udp)
        {
            await using var server = new TurnFixture(modern: true);
            var candidate = await peer.GatherRelayCandidateAsync(IceServerUri.Parse($"turn:localhost:{server.Server.Port}"), new(TurnFixture.Username, TurnFixture.Secret), Local(), TurnFixture.Fast(), ct);
            Check(candidate.Type == IceCandidateType.Relay && server.Allocations == 1);
            await peer.DisposeAsync(); Check(server.Allocations == 0 && server.Deletes == 1); return;
        }
        await using var stream = new TurnStreamFixture(transport == TurnServerTransport.Tls, serverName: wrongName ? "turn.fixture.local" : "localhost") { AllowTlsRejection = wrongName };
        var scheme = transport == TurnServerTransport.Tls ? "turns" : "turn";
        var uri = IceServerUri.Parse($"{scheme}:localhost:{stream.Server.Port}?transport=tcp");
        var options = stream.Options;
        if (options.Tls != null) options = options with { Tls = options.Tls with { ServerName = "ignored.override.invalid" } };
        try
        {
            var candidate = await peer.GatherRelayCandidateAsync(uri, new(TurnFixture.Username, TurnFixture.Secret), Local(), options, ct);
            Check(!wrongName && candidate.Type == IceCandidateType.Relay && stream.Backend.Allocations == 1);
            await peer.DisposeAsync(); Check(stream.Backend.Allocations == 0 && stream.Backend.Deletes == 1);
        }
        catch (AuthenticationException) when (wrongName) { Check(stream.Backend.Requests == 0, "Credentials sent before URI identity validation"); }
    }
}
