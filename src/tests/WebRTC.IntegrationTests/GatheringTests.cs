using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class GatheringTests
{
    private static void Check(bool value, string message = "STUN gathering assertion failed") => SctpTests.Check(value, message);
    internal static StunGatheringOptions Fast(bool fingerprint = false) => new()
    { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100), MaximumRequests = 3, FinalWaitMultiplier = 2, Timeout = TimeSpan.FromSeconds(2), RequireFingerprint = fingerprint };
    internal static Socket Server(IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp); socket.Bind(new IPEndPoint(address, 0)); return socket;
    }
    internal static byte[] Response(byte[] request, IPEndPoint mapped, bool fingerprint = true,
        bool duplicate = false, ushort attribute = 0, ushort type = 0x0101)
    {
        var bytes = new byte[128]; var writer = new StunMessageWriter(bytes, type, request.AsSpan(8, 12));
        Check(writer.TryAddXorMappedAddress(mapped)); if (duplicate) Check(writer.TryAddXorMappedAddress(mapped));
        if (attribute != 0) Check(writer.TryAddUInt32(attribute, 123));
        Check(writer.TryComplete([], fingerprint, out var size)); return bytes[..size];
    }
    internal static async Task<(byte[] Request, IPEndPoint Source)> Request(Socket server, CancellationToken ct)
    {
        var bytes = new byte[2048]; var read = await server.ReceiveFromAsync(bytes, SocketFlags.None,
            new IPEndPoint(server.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), ct);
        var request = bytes[..read.ReceivedBytes];
        Check(StunMessage.TryParse(request, out var message) && message.Type == StunMessage.BindingRequest && message.VerifyFingerprint());
        return (request, (IPEndPoint)read.RemoteEndPoint);
    }
    internal static async Task<IPEndPoint> ReplyOnce(Socket server, CancellationToken ct, bool fingerprint = true, IPEndPoint? mapping = null)
    {
        var request = await Request(server, ct);
        await server.SendToAsync(Response(request.Request, mapping ?? request.Source, fingerprint), SocketFlags.None, request.Source, ct);
        return request.Source;
    }
    internal static async Task Exchange(bool ipv6, bool fingerprint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var server = Server(address); await using var ice = new IceUdpTransport(new(address, 0));
        var response = ReplyOnce(server, ct, fingerprint);
        var boundCopy = ice.LocalEndPoint; boundCopy.Port = 1;
        var candidate = await ice.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, Fast(fingerprint), ct);
        Check((await response).Equals(ice.LocalEndPoint) && candidate.EndPoint.Equals(ice.LocalEndPoint) && candidate.RelatedEndPoint!.Equals(ice.LocalEndPoint));
        Check(candidate.Type == IceCandidateType.ServerReflexive && candidate.Priority == 1694498815);
        var copy = candidate.RelatedEndPoint!; copy.Port = 1;
        Check(candidate.RelatedEndPoint!.Port == ice.LocalEndPoint.Port && candidate.ToSdpAttribute("test").Contains("typ srflx raddr", StringComparison.Ordinal));
        Check(ice.GetGatheringDiagnostics() is { ActiveTransactions: 0, SentRequests: 1, Retransmissions: 0, SuccessfulBindings: 1 });
        Check(!ice.IsConnected && !ice.Completion.IsCompleted, "Binding response nominated or stopped ICE");
    }
    internal static async Task Retransmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var server = Server(); await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0));
        var gathering = ice.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, Fast(), ct);
        var first = await Request(server, ct);
        for (var i = 0; i < 2; i++)
        {
            var next = await Request(server, ct);
            Check(first.Request.AsSpan().SequenceEqual(next.Request) && first.Source.Equals(next.Source), "STUN retry changed transaction/socket");
        }
        await server.SendToAsync(Response(first.Request, first.Source), SocketFlags.None, first.Source, ct);
        Check((await gathering).EndPoint.Equals(first.Source));
        Check(ice.GetGatheringDiagnostics() is { SentRequests: 3, Retransmissions: 2, SuccessfulBindings: 1, ActiveTransactions: 0 });
    }
    internal static async Task Hostile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var server = Server(); using var attacker = Server(); await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0));
        var gathering = ice.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, Fast(true), ct);
        var request = await Request(server, ct); var valid = Response(request.Request, request.Source);
        var wrongId = request.Request.ToArray(); wrongId[8] ^= 1;
        await server.SendToAsync(Response(wrongId, request.Source), SocketFlags.None, request.Source, ct);
        await attacker.SendToAsync(valid, SocketFlags.None, request.Source, ct);
        var badCrc = valid.ToArray(); badCrc[^1] ^= 1;
        var invalid = new[] { badCrc, Response(request.Request, request.Source, duplicate: true),
            Response(request.Request, request.Source, false), Response(request.Request, new(IPAddress.IPv6Loopback, 100)),
            Response(request.Request, new(IPAddress.Loopback, 0)), Response(request.Request, new(IPAddress.Parse("224.0.0.1"), 100)) };
        foreach (var packet in invalid) await server.SendToAsync(packet, SocketFlags.None, request.Source, ct);
        while (ice.GetGatheringDiagnostics().RejectedResponses < 7) await Task.Delay(5, ct);
        Check(!gathering.IsCompleted && !ice.IsConnected);
        // Unknown optional attributes are ignored; the expected transaction remains usable.
        await server.SendToAsync(Response(request.Request, request.Source, attribute: 0xABCD), SocketFlags.None, request.Source, ct);
        Check((await gathering).EndPoint.Equals(request.Source));
    }
    internal static async Task RequiredAttributeOrError(bool required)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var server = Server(); await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0));
        var gathering = ice.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, Fast(), ct);
        var request = await Request(server, ct);
        byte[] packet;
        if (required) packet = Response(request.Request, request.Source, attribute: 0x1234);
        else
        {
            var bytes = new byte[64]; var writer = new StunMessageWriter(bytes, 0x0111, request.Request.AsSpan(8, 12));
            Check(writer.TryAddAttribute(0x0009, [0, 0, 3, 0])); // Redirect must not contact an unrequested server.
            Check(writer.TryComplete([], true, out var size)); packet = bytes[..size];
        }
        await server.SendToAsync(packet, SocketFlags.None, request.Source, ct);
        try { await gathering; throw new InvalidOperationException("Unsafe STUN response accepted"); }
        catch (InvalidDataException) when (required) { }
        catch (IOException) when (!required) { }
        Check(!ice.Completion.IsCompleted && ice.GetGatheringDiagnostics().ActiveTransactions == 0);
    }
    internal static async Task Cancellation(bool deadline)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource(); using var server = Server();
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0));
        var gathering = ice.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, Fast() with
            { Timeout = TimeSpan.FromMilliseconds(150) }, cancel.Token);
        await Request(server, timeout.Token); if (!deadline) cancel.Cancel();
        try { await gathering; throw new InvalidOperationException("Silent STUN gather succeeded"); }
        catch (OperationCanceledException) when (!deadline) { }
        catch (TimeoutException) when (deadline) { }
        Check(ice.GetGatheringDiagnostics().ActiveTransactions == 0 && !ice.Completion.IsCompleted);
        await using var other = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await Task.WhenAll(ice.ConnectAsync(other.LocalCredentials, IceRole.Controlling, [new(other.LocalEndPoint)], timeout.Token),
            other.ConnectAsync(ice.LocalCredentials, IceRole.Controlled, [new(ice.LocalEndPoint)], timeout.Token));
        Check(ice.IsConnected && other.IsConnected, "Gather cancellation stopped the owned socket");
    }
    internal static async Task AdmissionAndDisposal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); var servers = new List<Socket>(); var pending = new List<Task<IceCandidate>>();
        try
        {
            for (var i = 0; i < 9; i++) servers.Add(Server());
            for (var i = 0; i < 8; i++) pending.Add(ice.GatherServerReflexiveCandidateAsync((IPEndPoint)servers[i].LocalEndPoint!, cancellationToken: ct));
            Check(ice.GetGatheringDiagnostics().ActiveTransactions == 8);
            foreach (var server in new[] { servers[0], servers[8] })
                try { await ice.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, cancellationToken: ct); throw new IOException("Unbounded STUN admission"); }
                catch (InvalidOperationException) { }
            await ice.DisposeAsync();
            foreach (var task in pending)
                try { await task.WaitAsync(ct); throw new IOException("Disposed gather succeeded"); }
                catch (OperationCanceledException) { }
            Check(ice.GetGatheringDiagnostics().ActiveTransactions == 0);
        }
        finally { foreach (var server in servers) server.Dispose(); }
    }
    internal static async Task PeerSignaling()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var server = Server(); await using var peer = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) });
        var offer = peer.CreateOffer(); var original = SdpSessionDescription.Parse(offer);
        Check(!offer.Contains("a=end-of-candidates", StringComparison.Ordinal) && offer.Contains("a=ice-options:trickle", StringComparison.Ordinal));
        var gathering = peer.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, Fast(true), ct);
        var request = await Request(server, ct);
        try { peer.CompleteGathering(); throw new IOException("Gather marked complete with active requests"); } catch (InvalidOperationException) { }
        var mapped = new IPEndPoint(IPAddress.Parse("127.0.0.2"), request.Source.Port);
        await server.SendToAsync(Response(request.Request, mapped), SocketFlags.None, request.Source, ct);
        var candidate = await gathering;
        Check(candidate.EndPoint.Equals(mapped) && peer.GetLocalCandidates().Count == 2);
        var updated = SdpSessionDescription.Parse(peer.LocalDescription!);
        Check(updated.Media.All(m => m.Candidates.Count == 2 && m.Candidates.Any(c => c.Type == IceCandidateType.ServerReflexive)));
        Check(updated.Media[0].FingerprintSha256 == original.Media[0].FingerprintSha256 &&
            peer.LocalDescription!.Split("\r\n")[1] == offer.Split("\r\n")[1]);
        peer.CompleteGathering();
        Check(peer.LocalDescription!.Split("a=end-of-candidates").Length == 3);
        try { await peer.GatherServerReflexiveCandidateAsync((IPEndPoint)server.LocalEndPoint!, cancellationToken: ct); throw new IOException("Completed gathering restarted"); }
        catch (InvalidOperationException) { }
        await using var remote = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) });
        peer.SetRemoteAnswer(remote.CreateAnswer(peer.LocalDescription!));
        await Task.WhenAll(peer.ConnectAsync(ct), remote.ConnectAsync(ct));
        Check(peer.State == PeerConnectionState.Connected && remote.State == PeerConnectionState.Connected);
    }
}
