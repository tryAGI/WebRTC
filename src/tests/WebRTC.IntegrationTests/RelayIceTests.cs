using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using tryAGI.WebRTC;

internal static class RelayIceTests
{
    private static void Check(bool value, string message = "Relay ICE assertion failed") => TurnFixture.Check(value, message);
    private static IceUdpTransportOptions Fast(bool relayOnly = true) => new()
    { RelayOnly = relayOnly, CheckInterval = TimeSpan.FromMilliseconds(10), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100), ConnectionTimeout = TimeSpan.FromSeconds(5) };
    private static Task<IceCandidate> Gather(IceUdpTransport transport, TurnFixture fixture, CancellationToken ct) =>
        transport.GatherRelayCandidateAsync(fixture.Server, new(TurnFixture.Username, TurnFixture.Secret), TurnFixture.Fast(), ct);
    private static async Task<byte[]> Read(IceUdpTransport transport, CancellationToken ct)
    { await foreach (var data in transport.ReceiveDatagramsAsync(ct)) return data; throw new IOException("ICE reader ended"); }
    internal static async Task Exchange(bool ipv6, bool twoRelays, bool late = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        await using var leftServer = new TurnFixture(ipv6); await using var rightServer = new TurnFixture(ipv6);
        await using var left = new IceUdpTransport(new(ip, 0), options: Fast());
        await using var right = new IceUdpTransport(new(ip, 0), options: Fast(twoRelays));
        var remote = twoRelays ? await Gather(right, rightServer, ct) : new IceCandidate(right.LocalEndPoint);
        Task leftConnection, rightConnection;
        IceCandidate local;
        if (late)
        {
            leftConnection = left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [remote], ct);
            rightConnection = right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [], ct);
            local = await Gather(left, leftServer, ct);
            right.AddRemoteCandidate(local);
        }
        else
        {
            local = await Gather(left, leftServer, ct);
            leftConnection = left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [remote], ct);
            rightConnection = right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [local], ct);
        }
        await Task.WhenAll(leftConnection, rightConnection);
        Check(left.GetDiagnostics() is { SelectedLocalCandidateType: IceCandidateType.Relay, LocalPaths: 1, PendingRelayPermissions: 0 });
        Check(left.GetDiagnostics().SelectedLocalEndPoint!.Equals(local.EndPoint));
        Check(right.GetDiagnostics().SelectedRemoteEndPoint!.Equals(local.EndPoint));
        if (twoRelays) Check(right.GetDiagnostics().SelectedLocalCandidateType == IceCandidateType.Relay);
        await left.SendDatagramAsync("\u0016through-owned-relay"u8.ToArray(), ct);
        Check((await Read(right, ct)).AsSpan().SequenceEqual("\u0016through-owned-relay"u8));
        await right.SendDatagramAsync("\u0080return-through-relay"u8.ToArray(), ct);
        Check((await Read(left, ct)).AsSpan().SequenceEqual("\u0080return-through-relay"u8));
        await left.DisposeAsync(); Check(leftServer.Deletes == 1 && leftServer.Allocations == 0);
        if (twoRelays) { await right.DisposeAsync(); Check(rightServer.Deletes == 1); }
    }
    internal static async Task PathBinding()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7)); var ct = deadline.Token;
        await using var fixture = new TurnFixture();
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false));
        using var remote = TurnFixture.Socket(IPAddress.Loopback);
        var credentials = IceCredentials.Generate(); var relay = await Gather(left, fixture, ct);
        var connection = left.ConnectAsync(credentials, IceRole.Controlling, [new((IPEndPoint)remote.LocalEndPoint!)], ct);
        var buffer = new byte[2048]; bool wrongPathExercised = false;
        while (!connection.IsCompleted)
        {
            var read = await remote.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            if (!read.RemoteEndPoint.Equals(relay.EndPoint)) continue; // Deliberately never answer the host path.
            var reply = Response(buffer.AsSpan(0, read.ReceivedBytes), relay.EndPoint, credentials.Password);
            if (!wrongPathExercised)
            {
                await remote.SendToAsync(reply, SocketFlags.None, left.LocalEndPoint, ct);
                await Task.Delay(60, ct);
                Check(!left.IsConnected && left.GetDiagnostics().LastCheckRoundTripTime == null, "A relay transaction accepted the right source on the wrong local socket");
                wrongPathExercised = true;
            }
            await remote.SendToAsync(reply, SocketFlags.None, relay.EndPoint, ct);
            // Nomination may complete on the reader; avoid waiting indefinitely for another packet.
            await Task.WhenAny(connection, Task.Delay(20, ct));
        }
        await connection; Check(wrongPathExercised && left.GetDiagnostics() is { SelectedLocalCandidateType: IceCandidateType.Relay, LocalPaths: 2 });
        var consent = new byte[256]; var consentWriter = new StunMessageWriter(consent, 1, RandomNumberGenerator.GetBytes(12));
        Check(consentWriter.TryAddAttribute(6, Encoding.ASCII.GetBytes(left.LocalCredentials.UsernameFragment + ":" + credentials.UsernameFragment)));
        Check(consentWriter.TryComplete(Encoding.ASCII.GetBytes(left.LocalCredentials.Password), true, out var consentLength));
        var validated = left.GetDiagnostics().ValidatedRequests;
        await remote.SendToAsync(consent.AsMemory(0, consentLength), SocketFlags.None, left.LocalEndPoint, ct);
        await Task.Delay(60, ct);
        Check(left.GetDiagnostics().ValidatedRequests == validated, "Consent accepted the selected source on an unselected local path");
        await remote.SendToAsync(consent.AsMemory(0, consentLength), SocketFlags.None, relay.EndPoint, ct);
        while (left.GetDiagnostics().ValidatedRequests == validated) await Task.Delay(5, ct);
        var dropped = left.GetDiagnostics().DroppedDatagrams;
        await remote.SendToAsync("\u0016wrong-path"u8.ToArray(), SocketFlags.None, left.LocalEndPoint, ct);
        while (left.GetDiagnostics().DroppedDatagrams == dropped) await Task.Delay(5, ct);
        await remote.SendToAsync("\u0016right-path"u8.ToArray(), SocketFlags.None, relay.EndPoint, ct);
        Check((await Read(left, ct)).AsSpan().SequenceEqual("\u0016right-path"u8), "Selected source bypassed local-path media binding");
    }
    internal static async Task NominationBinding()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = deadline.Token;
        await using var fixture = new TurnFixture();
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false));
        using var remote = TurnFixture.Socket(IPAddress.Loopback);
        var credentials = IceCredentials.Generate(); var relay = await Gather(left, fixture, ct);
        var connection = left.ConnectAsync(credentials, IceRole.Controlled, [new((IPEndPoint)remote.LocalEndPoint!)], ct);
        var buffer = new byte[2048];
        while (true)
        {
            var read = await remote.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            if (!read.RemoteEndPoint.Equals(relay.EndPoint)) continue;
            await remote.SendToAsync(Response(buffer.AsSpan(0, read.ReceivedBytes), relay.EndPoint, credentials.Password), SocketFlags.None, relay.EndPoint, ct);
            break;
        }
        while (left.GetDiagnostics().LastCheckRoundTripTime == null) await Task.Delay(5, ct);
        var nomination = new byte[256]; var writer = new StunMessageWriter(nomination, 1, RandomNumberGenerator.GetBytes(12));
        Check(writer.TryAddAttribute(6, Encoding.ASCII.GetBytes(left.LocalCredentials.UsernameFragment + ":" + credentials.UsernameFragment)));
        Check(writer.TryAddUInt32(0x0024, 1862270975) && writer.TryAddUInt64(0x802A, 42) && writer.TryAddAttribute(0x0025, []));
        Check(writer.TryComplete(Encoding.ASCII.GetBytes(left.LocalCredentials.Password), true, out var size));
        await remote.SendToAsync(nomination.AsMemory(0, size), SocketFlags.None, left.LocalEndPoint, ct);
        while (left.GetDiagnostics().ValidatedRequests == 0) await Task.Delay(5, ct);
        Check(!connection.IsCompleted && !left.IsConnected, "Nomination on host selected the validated relay pair");
        await remote.SendToAsync(nomination.AsMemory(0, size), SocketFlags.None, relay.EndPoint, ct);
        await connection; Check(left.GetDiagnostics().SelectedLocalCandidateType == IceCandidateType.Relay);
    }
    private static byte[] Response(ReadOnlySpan<byte> packet, IPEndPoint mapped, string password)
    {
        Check(StunMessage.TryParse(packet, out var request));
        var bytes = new byte[256]; var writer = new StunMessageWriter(bytes, 0x0101, request.TransactionId);
        Check(writer.TryAddXorMappedAddress(mapped)); Check(writer.TryComplete(Encoding.ASCII.GetBytes(password), true, out var length));
        return bytes[..length];
    }
    internal static async Task CancellationAndBounds()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false));
        await using var held = new TurnFixture { HoldMethod = 3, HoldMilliseconds = 250 };
        using var cancel = new CancellationTokenSource();
        var gather = Gather(left, held, cancel.Token);
        while (held.Requests < 2) await Task.Delay(5, ct);
        cancel.Cancel();
        try { await gather; throw new IOException("Canceled relay attached"); } catch (OperationCanceledException) { }
        Check(left.GetDiagnostics().LocalPaths == 1 && !left.Completion.IsCompleted);
        await using var a = new TurnFixture(); await using var b = new TurnFixture(); await using var c = new TurnFixture();
        await Task.WhenAll(Gather(left, a, ct), Gather(left, b, ct), Gather(left, c, ct));
        Check(left.GetDiagnostics().LocalPaths == 4);
        try { await Gather(left, held, ct); throw new IOException("Fourth relay admitted"); } catch (InvalidOperationException) { }
        await left.DisposeAsync(); Check(a.Deletes == 1 && b.Deletes == 1 && c.Deletes == 1);
    }
    internal static async Task PermissionDisposal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = deadline.Token;
        await using var fixture = new TurnFixture { HoldMethod = 8, HoldMilliseconds = 250 };
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
        await Gather(left, fixture, ct);
        var pending = left.ConnectAsync(IceCredentials.Generate(), IceRole.Controlling, [new(fixture.Peer)], ct);
        while (fixture.Requests < 3) await Task.Delay(5, ct);
        Check(left.GetDiagnostics() is { SentChecks: 0, PendingRelayPermissions: 1 });
        var disposing = left.DisposeAsync().AsTask(); var secondDisposal = left.DisposeAsync().AsTask();
        Check(!secondDisposal.IsCompleted || disposing.IsCompleted, "Concurrent disposal returned before owned workers joined");
        await Task.WhenAll(disposing, secondDisposal).WaitAsync(ct);
        try { await pending; throw new IOException("Disposed ICE completed connection"); } catch (OperationCanceledException) { }
        Check(fixture.Deletes >= 1 && fixture.Allocations == 0 && left.Completion.IsCompleted, $"Permission disposal deletes={fixture.Deletes}, requests={fixture.Requests}, complete={left.Completion.IsCompleted}");
    }
    internal static async Task SelectedExpiry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7)); var ct = deadline.Token;
        await using var fixture = new TurnFixture { Lifetime = 2, HoldRefresh = true };
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false));
        var relay = await Gather(left, fixture, ct);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [relay], ct));
        Check(await left.Completion.WaitAsync(ct) is IOException && !left.IsConnected, "Selected relay expiry silently retained connectivity");
        try { await left.SendDatagramAsync("\u0016expired"u8.ToArray(), ct); throw new InvalidOperationException("Expired relay sent traffic"); } catch (IOException) { }
    }
    internal static async Task HostSurvivesUnselectedExpiry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = deadline.Token;
        await using var fixture = new TurnFixture { Lifetime = 2, HoldRefresh = true };
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false));
        await Gather(left, fixture, ct);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        Check(left.GetDiagnostics().SelectedLocalCandidateType == IceCandidateType.Host);
        while (left.GetDiagnostics().LocalPaths != 1) await Task.Delay(10, ct);
        Check(left.IsConnected && !left.Completion.IsCompleted, "Unselected relay expiry killed the healthy host pair");
        await left.SendDatagramAsync("\u0016host-survives"u8.ToArray(), ct);
        Check((await Read(right, ct)).AsSpan().SequenceEqual("\u0016host-survives"u8));
    }
    internal static async Task PairBudget()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var fixture = new TurnFixture();
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false) with { MaximumCandidatePairs = 1 });
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast(false));
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        try { await Gather(left, fixture, ct); throw new IOException("Relay exceeded pair budget"); } catch (InvalidOperationException) { }
        Check(fixture.Deletes == 1 && fixture.Allocations == 0 && left.GetDiagnostics() is { LocalPaths: 1, CandidatePairs: 1 });
        Check(left.IsConnected);
    }
    internal static async Task PionSameServer(Uri uri, bool same)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var http = new HttpClient { BaseAddress = uri };
        async Task<TurnSession> NewServer()
        { using var response = await http.PostAsync("turn", null, ct); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync(TurnJson.Default.TurnSession, ct))!; }
        var a = await NewServer(); var b = same ? a : await NewServer();
        try
        {
            await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
            await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
            var x = await left.GatherRelayCandidateAsync(new(IPAddress.Loopback, a.Port), new(a.Username, a.Password), TurnFixture.Fast(), ct);
            var y = await right.GatherRelayCandidateAsync(new(IPAddress.Loopback, b.Port), new(b.Username, b.Password), TurnFixture.Fast(), ct);
            Check((await http.GetFromJsonAsync("turn/" + a.Id, TurnJson.Default.TurnStats, ct))!.Allocations == (same ? 2 : 1));
            await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [y], ct), right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [x], ct));
            await left.SendDatagramAsync("\u0016two-relays"u8.ToArray(), ct); Check((await Read(right, ct)).AsSpan().SequenceEqual("\u0016two-relays"u8));
            await right.SendDatagramAsync("\u0016two-relays-return"u8.ToArray(), ct); Check((await Read(left, ct)).AsSpan().SequenceEqual("\u0016two-relays-return"u8));
            await left.DisposeAsync(); await right.DisposeAsync();
            Check((await http.GetFromJsonAsync("turn/" + a.Id, TurnJson.Default.TurnStats, ct))!.Allocations == 0);
            if (!same) Check((await http.GetFromJsonAsync("turn/" + b.Id, TurnJson.Default.TurnStats, ct))!.Allocations == 0);
        }
        finally
        {
            using var one = await http.DeleteAsync("turn/" + a.Id, CancellationToken.None); one.EnsureSuccessStatusCode();
            if (!same) { using var two = await http.DeleteAsync("turn/" + b.Id, CancellationToken.None); two.EnsureSuccessStatusCode(); }
        }
    }
}
