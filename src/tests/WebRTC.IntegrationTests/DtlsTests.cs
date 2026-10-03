using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Authentication;
using tryAGI.WebRTC;

internal static class DtlsTests
{
    private static IceUdpTransport Ice() => new(new(IPAddress.Loopback, 0), options: new()
    { CheckInterval = TimeSpan.FromMilliseconds(10), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100), ConnectionTimeout = TimeSpan.FromSeconds(3) });
    private static DtlsSrtpOptions Options(SrtpProfile profile, bool cookie, int mtu) => new()
    { Profiles = [profile], RequireCookie = cookie, MaximumDatagramSize = mtu, InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100), HandshakeTimeout = TimeSpan.FromSeconds(4) };
    private static void Check(bool success, string message = "DTLS assertion failed")
    { if (!success) throw new InvalidOperationException(message); }

    internal static async Task Exchange(SrtpProfile profile, bool cookie = false, int mtu = 1200, string fault = "")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var left = Ice(); await using var right = Ice();
        await using var proxy = new DtlsProxy(left.LocalEndPoint, right.LocalEndPoint, fault);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(proxy.EndPoint)], deadline.Token),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(proxy.EndPoint)], deadline.Token));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        await using var client = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256(), Options(profile, false, mtu));
        await using var server = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), Options(profile, cookie, mtu));
        await Task.WhenAll(client.ConnectAsync(deadline.Token), server.ConnectAsync(deadline.Token));
        Check(client.IsConnected && server.IsConnected);
        Check(client.GetDiagnostics().Profile == profile && server.GetDiagnostics().Profile == profile);
        await client.SendApplicationDatagramAsync("authenticated-client"u8.ToArray(), deadline.Token);
        Check((await App(server, deadline.Token)).AsSpan().SequenceEqual("authenticated-client"u8));
        await server.SendApplicationDatagramAsync("authenticated-server"u8.ToArray(), deadline.Token);
        Check((await App(client, deadline.Token)).AsSpan().SequenceEqual("authenticated-server"u8));
        foreach (var (sender, receiver) in new[] { (client, server), (server, client) })
        {
            var rtp = Convert.FromHexString("806f00010000000200000003aabb");
            await sender.SendRtpAsync(rtp, deadline.Token);
            var received = await Media(receiver, deadline.Token);
            Check(received.Kind == SecureMediaKind.Rtp && received.Data.AsSpan().SequenceEqual(rtp), "Exporter/SRTP directional keys disagree");
            var rtcp = Convert.FromHexString("80c9000100000003");
            await sender.SendRtcpAsync(rtcp, deadline.Token);
            received = await Media(receiver, deadline.Token);
            Check(received.Kind == SecureMediaKind.Rtcp && received.Data.AsSpan().SequenceEqual(rtcp));
        }
        if (fault.Length != 0) Check(proxy.Affected > 0, "Fault path was not exercised");
        if (fault is "hello-loss" or "final-loss" or "fragment-loss")
            Check(client.GetDiagnostics().Retransmissions + server.GetDiagnostics().Retransmissions > 0);
        if (fault == "app-replay")
        {
            // The proxy sends a corrupted record, the original, and its replay.
            while (server.GetDiagnostics().RejectedRecords < 2) await Task.Delay(5, deadline.Token);
            using var noExtra = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await App(server, noExtra.Token); throw new InvalidOperationException("Replay delivered application data"); }
            catch (OperationCanceledException) { }
        }
        if (mtu == 256) Check(proxy.Fragments > 0, "Certificate fragmentation was not exercised");
    }

    internal static async Task Rejection(string fault)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var left = Ice(); await using var right = Ice();
        await using var proxy = new DtlsProxy(left.LocalEndPoint, right.LocalEndPoint, fault);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(proxy.EndPoint)], deadline.Token),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(proxy.EndPoint)], deadline.Token));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        var fingerprint = b.GetFingerprintSha256(); if (fault == "fingerprint") fingerprint[0] ^= 1;
        await using var client = new DtlsSrtpTransport(left, a, DtlsRole.Client, fingerprint, Options(SrtpProfile.AeadAes128Gcm, false, 1200));
        await using var server = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), Options(SrtpProfile.AeadAes128Gcm, false, 1200));
        var serverConnect = server.ConnectAsync(deadline.Token);
        try { await client.ConnectAsync(deadline.Token); throw new InvalidOperationException("Unauthenticated peer connected"); }
        catch (AuthenticationException) { }
        Check(!client.IsConnected && await client.Completion is AuthenticationException);
        await server.DisposeAsync();
        try { await serverConnect; } catch (OperationCanceledException) { }
        if (fault == "signature") Check(proxy.Affected > 0);
    }

    internal static async Task Pion(Uri uri, DtlsRole role, SrtpProfile profile, int mtu = 1200)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await using var ice = Ice(); using var identity = DtlsIdentity.Generate();
        var endpoint = ice.LocalEndPoint;
        var offer = new DtlsOffer(true, true, role == DtlsRole.Server,
            Convert.ToHexString(identity.GetFingerprintSha256()), (ushort)profile, mtu,
            ice.LocalCredentials.UsernameFragment, ice.LocalCredentials.Password,
            $"1 1 udp 2130706431 {endpoint.Address} {endpoint.Port} typ host");
        using var response = await http.PostAsJsonAsync(new Uri(uri, "/peer"), offer, InteropJson.Default.DtlsOffer, deadline.Token);
        response.EnsureSuccessStatusCode();
        var remote = await response.Content.ReadFromJsonAsync(InteropJson.Default.DtlsDescription, deadline.Token)
            ?? throw new IOException("Missing independent DTLS peer");
        Check(IPAddress.TryParse(remote.Address, out var ip) && IPAddress.IsLoopback(ip));
        await ice.ConnectAsync(new(remote.Fragment, remote.Password), IceRole.Controlled, [new(new(ip!, remote.Port), remote.Priority)], deadline.Token);
        await using var secure = new DtlsSrtpTransport(ice, identity, role, Convert.FromHexString(remote.Fingerprint), Options(profile, false, mtu));
        await secure.ConnectAsync(deadline.Token);
        Check(secure.GetDiagnostics().Profile == profile);
        await secure.SendApplicationDatagramAsync("dtls-independent"u8.ToArray(), deadline.Token);
        Check((await App(secure, deadline.Token)).AsSpan().SequenceEqual("pion:dtls-independent"u8));
        var rtp = Convert.FromHexString("806f00010000000200000003aabb");
        await secure.SendRtpAsync(rtp, deadline.Token);
        var received = await Media(secure, deadline.Token);
        Check(received.Kind == SecureMediaKind.Rtp && received.Data.AsSpan().SequenceEqual(rtp));
        var rtcp = Convert.FromHexString("80c9000100000003");
        await secure.SendRtcpAsync(rtcp, deadline.Token);
        received = await Media(secure, deadline.Token);
        Check(received.Kind == SecureMediaKind.Rtcp && received.Data.AsSpan().SequenceEqual(rtcp));
        Console.WriteLine($"  Pion DTLS {role} authenticated handshake={secure.GetDiagnostics().HandshakeTime!.Value.TotalMilliseconds:F1} ms");
    }

    internal static async Task Cancel()
    {
        await using var left = Ice(); await using var right = Ice();
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)]),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)]));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        await using var client = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try { await client.ConnectAsync(cancel.Token); throw new InvalidOperationException("Silent peer connected"); }
        catch (OperationCanceledException) { }
        Check(await client.Completion is OperationCanceledException && !client.IsConnected);
        // DTLS owns its keys and reader, not the nominated ICE socket.
        Check(left.IsConnected && right.IsConnected);
    }

    internal static async Task MalformedFlood()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var left = Ice(); await using var right = Ice();
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], deadline.Token),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], deadline.Token));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        await using var client = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256());
        await using var server = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256());
        var serverConnect = server.ConnectAsync(deadline.Token);
        var packet = new byte[1125]; packet[0] = 22; packet[1] = 0xfe; packet[2] = 0xfd;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(11), 1112);
        packet[13] = 1; packet[14] = 0; packet[15] = 0x40; packet[16] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(17), 32); // Beyond the eight-message receive window.
        packet[22] = 0; packet[23] = 4; packet[24] = 0x4c; // 1100-byte fragment.
        for (var i = 0; i < 32; i++) await left.SendDatagramAsync(packet, deadline.Token);
        // A nominal empty handshake followed by an incomplete header must be rejected atomically.
        packet = new byte[26]; packet[0] = 22; packet[1] = 0xfe; packet[2] = 0xfd; packet[12] = 13; packet[13] = 1;
        await left.SendDatagramAsync(packet, deadline.Token);
        while (server.GetDiagnostics().RejectedRecords < 33) await Task.Delay(5, deadline.Token);
        Check(!server.IsConnected);
        await Task.WhenAll(client.ConnectAsync(deadline.Token), serverConnect);
        await client.SendApplicationDatagramAsync("after-malformed-input"u8.ToArray(), deadline.Token);
        Check((await App(server, deadline.Token)).AsSpan().SequenceEqual("after-malformed-input"u8));
    }

    internal static async Task Timeout()
    {
        await using var left = Ice(); await using var right = Ice();
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)]),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)]));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        await using var client = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256(),
            Options(SrtpProfile.AeadAes128Gcm, false, 1200) with { HandshakeTimeout = TimeSpan.FromMilliseconds(150) });
        try { await client.ConnectAsync(); throw new InvalidOperationException("Silent peer connected"); }
        catch (TimeoutException) { }
        Check(await client.Completion is TimeoutException, "Pending receive masked timeout");
    }

    internal static async Task<byte[]> App(DtlsSrtpTransport transport, CancellationToken ct)
    {
        await foreach (var bytes in transport.ReceiveApplicationDatagramsAsync(ct)) return bytes;
        throw new IOException("Required protected application datagram was not received");
    }
    internal static async Task<SecureMediaDatagram> Media(DtlsSrtpTransport transport, CancellationToken ct)
    {
        await foreach (var bytes in transport.ReceiveMediaDatagramsAsync(ct)) return bytes;
        throw new IOException("Required protected media datagram was not received");
    }
}

internal sealed class DtlsProxy : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _affected, _fragments;
    public int Affected => Volatile.Read(ref _affected);
    public int Fragments => Volatile.Read(ref _fragments);
    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public DtlsProxy(IPEndPoint left, IPEndPoint right, string fault)
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _worker = Task.Run(async () =>
        {
            var buffer = new byte[2048];
            byte[]? held = null;
            try
            {
                while (true)
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                    var fromLeft = result.RemoteEndPoint.Equals(left);
                    if (!fromLeft && !result.RemoteEndPoint.Equals(right)) continue;
                    var bytes = buffer.AsMemory(0, result.ReceivedBytes).ToArray();
                    var destination = fromLeft ? right : left;
                    var plainHandshake = bytes.Length >= 25 && bytes[0] == 22 && bytes[3] == 0 && bytes[4] == 0;
                    var fragment = plainHandshake && (bytes[19] != 0 || bytes[20] != 0 || bytes[21] != 0 ||
                        !bytes.AsSpan(14, 3).SequenceEqual(bytes.AsSpan(22, 3)));
                    if (fragment) Interlocked.Increment(ref _fragments);
                    if (fault == "ccs-reorder" && !fromLeft && bytes[0] == 20 && _affected == 0) { held = bytes; continue; }
                    if (fault == "ccs-reorder" && held != null && !fromLeft && bytes[0] == 22 && bytes[4] == 1)
                    {
                        await _socket.SendToAsync(bytes, SocketFlags.None, destination, _lifetime.Token);
                        await _socket.SendToAsync(held, SocketFlags.None, destination, _lifetime.Token);
                        held = null; Interlocked.Increment(ref _affected); continue;
                    }
                    if (fault == "reorder" && !fromLeft && plainHandshake)
                    {
                        if (bytes[13] == 2 && held == null && _affected == 0) { held = bytes; continue; }
                        if (held != null)
                        {
                            await _socket.SendToAsync(bytes, SocketFlags.None, destination, _lifetime.Token);
                            await _socket.SendToAsync(held, SocketFlags.None, destination, _lifetime.Token);
                            held = null; Interlocked.Increment(ref _affected); continue;
                        }
                    }
                    var drop = fault == "hello-loss" && fromLeft && plainHandshake && bytes[13] == 1 ||
                        fault == "final-loss" && !fromLeft && bytes.Length >= 13 && bytes[0] == 22 && bytes[4] == 1 ||
                        fault == "fragment-loss" && !fromLeft && fragment;
                    if (drop && _affected == 0) { Interlocked.Increment(ref _affected); continue; }
                    if (fault == "signature" && !fromLeft && plainHandshake && bytes[13] == 12)
                    { bytes[^1] ^= 1; Interlocked.Increment(ref _affected); }
                    if (fault == "app-replay" && fromLeft && bytes[0] == 23)
                    {
                        var corrupt = bytes.ToArray(); corrupt[^1] ^= 1;
                        await _socket.SendToAsync(corrupt, SocketFlags.None, destination, _lifetime.Token);
                        await _socket.SendToAsync(bytes, SocketFlags.None, destination, _lifetime.Token);
                        Interlocked.Increment(ref _affected);
                    }
                    await _socket.SendToAsync(bytes, SocketFlags.None, destination, _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    public async ValueTask DisposeAsync()
    { _lifetime.Cancel(); await _worker; _socket.Dispose(); _lifetime.Dispose(); }
}

internal sealed record DtlsOffer(bool Controlling, bool Secure, bool DtlsClient, string Fingerprint, ushort Profile, int Mtu,
    string Fragment, string Password, string Candidate);
internal sealed record DtlsDescription(string Fingerprint, string Fragment, string Password, string Address, int Port, uint Priority);
