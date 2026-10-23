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
        if (fault == "client-flight-loss-reorder")
        {
            Check(proxy.DroppedClientCertificates == 1 && proxy.ReorderedClientKeys == 1 &&
                proxy.ReorderedClientCcs == 1 && proxy.DroppedFinished == 1,
                "Every client-flight loss/reorder must be exercised");
            Check(client.GetDiagnostics().Retransmissions + server.GetDiagnostics().Retransmissions > 0,
                "Client-flight loss must exercise recovery");
        }
        if (fault == "combined-loss-reorder")
        {
            Check(proxy.DroppedHellos == 2 && proxy.DroppedCertificates == 1 && proxy.DroppedFinished == 1,
                "Every planned handshake loss must occur");
            Check(proxy.ReorderedFlights == 1, "Server handshake reorder was not exercised");
            Check(client.GetDiagnostics().Retransmissions + server.GetDiagnostics().Retransmissions >= 2,
                "Combined losses must exercise retransmission");
        }
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

    internal static async Task Pion(Uri uri, DtlsRole role, SrtpProfile profile, int mtu = 1200, string fault = "")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await using var ice = Ice(); using var identity = DtlsIdentity.Generate();
        await using var proxy = fault.Length == 0 ? null : new DtlsProxy(ice.LocalEndPoint, null, fault);
        var endpoint = proxy?.EndPoint ?? ice.LocalEndPoint;
        var offer = new DtlsOffer(true, true, role == DtlsRole.Server,
            Convert.ToHexString(identity.GetFingerprintSha256()), (ushort)profile, mtu,
            ice.LocalCredentials.UsernameFragment, ice.LocalCredentials.Password,
            $"1 1 udp 2130706431 {endpoint.Address} {endpoint.Port} typ host");
        using var response = await http.PostAsJsonAsync(new Uri(uri, "/peer"), offer, InteropJson.Default.DtlsOffer, deadline.Token);
        response.EnsureSuccessStatusCode();
        var remote = await response.Content.ReadFromJsonAsync(InteropJson.Default.DtlsDescription, deadline.Token)
            ?? throw new IOException("Missing independent DTLS peer");
        if (!IPAddress.TryParse(remote.Address, out var ip) || !IPAddress.IsLoopback(ip))
            throw new IOException("Independent peer must use loopback");
        var remoteEndpoint = new IPEndPoint(ip, remote.Port);
        proxy?.BindRemote(remoteEndpoint);
        await ice.ConnectAsync(new(remote.Fragment, remote.Password), IceRole.Controlled,
            [new(proxy?.EndPoint ?? remoteEndpoint, remote.Priority)], deadline.Token);
        var options = Options(profile, false, mtu);
        if (fault == "client-verify-blackhole") options = options with { HandshakeTimeout = TimeSpan.FromMilliseconds(900) };
        await using var secure = new DtlsSrtpTransport(ice, identity, role, Convert.FromHexString(remote.Fingerprint), options);
        if (fault == "client-verify-blackhole")
        {
            try { await secure.ConnectAsync(deadline.Token); throw new IOException("Missing client authentication connected to Pion"); }
            catch (TimeoutException) { }
            Check(proxy is not null && proxy.DroppedClientVerifies > 1, "Independent peer fault was not repeated");
            var frozen = secure.GetHandshakeCounters();
            Check(frozen.Frozen && frozen.VerifiedFinishedMessages == 0 && frozen.CompletedFlights > 1 &&
                secure.GetDiagnostics().RejectedRecords == 0, "Independent loss control misattributed failure");
            await secure.DisposeAsync();
            Check(secure.GetHandshakeCounters() == frozen, "Independent timeout counters did not survive disposal");
            return;
        }
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
        if (fault == "client-flight-loss-reorder")
            Check(proxy is not null && proxy.DroppedClientCertificates == 1 && proxy.ReorderedClientKeys == 1 &&
                proxy.ReorderedClientCcs == 1 && proxy.DroppedFinished == 1, "Independent peer did not exercise every flight fault");
        var duration = secure.GetDiagnostics().HandshakeTime ?? throw new IOException("Missing authenticated handshake timing");
        Console.WriteLine($"  Pion DTLS {role} authenticated handshake={duration.TotalMilliseconds:F1} ms");
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

    internal static async Task MissingClientCertificateVerify()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var left = Ice(); await using var right = Ice();
        await using var proxy = new DtlsProxy(left.LocalEndPoint, right.LocalEndPoint, "client-verify-blackhole");
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(proxy.EndPoint)], deadline.Token),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(proxy.EndPoint)], deadline.Token));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        var options = Options(SrtpProfile.AeadAes128Gcm, false, 256) with
        { HandshakeTimeout = TimeSpan.FromMilliseconds(900) };
        await using var client = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256(), options);
        await using var server = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), options);
        var clientConnect = client.ConnectAsync(deadline.Token);
        var serverConnect = server.ConnectAsync(deadline.Token);
        foreach (var connecting in new[] { clientConnect, serverConnect })
        {
            try { await connecting; throw new InvalidOperationException("Missing CertificateVerify established DTLS"); }
            catch (TimeoutException) { }
        }
        Check(proxy.DroppedClientVerifies > 1, "CertificateVerify was not lost on initial and retransmitted flights");
        Check(!client.IsConnected && !server.IsConnected, "Missing client authentication must fail closed");
        Check(client.GetDiagnostics().RejectedRecords == 0 && server.GetDiagnostics().RejectedRecords == 0,
            "Loss alone must not fabricate a record rejection");
        Check(client.GetDiagnostics().Retransmissions > 0, "No client final-flight retransmission observed");
        Check(await client.Completion is TimeoutException && await server.Completion is TimeoutException,
            "Loss must retain timeout attribution after both receive loops end");
        var counters = server.GetHandshakeCounters();
        Check(counters.Frozen && counters.VerifiedFinishedMessages == 0 && counters.PendingHandshakeMessages > 0 &&
            counters.PendingHandshakeBytes > 0, "Incomplete handshake assembly was not retained on timeout");
        await server.DisposeAsync();
        Check(server.GetHandshakeCounters() == counters, "Disposal erased incomplete-assembly evidence");
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
    private readonly TaskCompletionSource<IPEndPoint> _remote = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void BindRemote(IPEndPoint remote)
    {
        if (!IPAddress.IsLoopback(remote.Address) || !_remote.TrySetResult(remote))
            throw new InvalidOperationException("Proxy remote must be bound once to loopback");
    }
    private int _affected, _fragments, _droppedHellos, _droppedCertificates, _droppedFinished, _reorderedFlights;
    private int _droppedClientCertificates, _reorderedClientKeys, _reorderedClientCcs, _droppedClientVerifies;
    public int DroppedClientCertificates => Volatile.Read(ref _droppedClientCertificates);
    public int ReorderedClientKeys => Volatile.Read(ref _reorderedClientKeys);
    public int ReorderedClientCcs => Volatile.Read(ref _reorderedClientCcs);
    public int DroppedClientVerifies => Volatile.Read(ref _droppedClientVerifies);
    public int Affected => Volatile.Read(ref _affected);
    public int Fragments => Volatile.Read(ref _fragments);
    public int DroppedHellos => Volatile.Read(ref _droppedHellos);
    public int DroppedCertificates => Volatile.Read(ref _droppedCertificates);
    public int DroppedFinished => Volatile.Read(ref _droppedFinished);
    public int ReorderedFlights => Volatile.Read(ref _reorderedFlights);
    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public DtlsProxy(IPEndPoint left, IPEndPoint? right, string fault)
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        if (right is not null) BindRemote(right);
        _worker = Task.Run(async () =>
        {
            var buffer = new byte[2048];
            byte[]? held = null, heldClientKey = null, heldClientCcs = null;
            try
            {
                var remote = await _remote.Task.WaitAsync(_lifetime.Token);
                while (true)
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                    var fromLeft = result.RemoteEndPoint.Equals(left);
                    if (!fromLeft && !result.RemoteEndPoint.Equals(remote)) continue;
                    var bytes = buffer.AsMemory(0, result.ReceivedBytes).ToArray();
                    var destination = fromLeft ? remote : left;
                    var plainHandshake = bytes.Length >= 25 && bytes[0] == 22 && bytes[3] == 0 && bytes[4] == 0;
                    var fragment = plainHandshake && (bytes[19] != 0 || bytes[20] != 0 || bytes[21] != 0 ||
                        !bytes.AsSpan(14, 3).SequenceEqual(bytes.AsSpan(22, 3)));
                    if (fragment) Interlocked.Increment(ref _fragments);
                    if (fault == "client-verify-blackhole" && fromLeft && plainHandshake && bytes[13] == 15)
                    { Interlocked.Increment(ref _droppedClientVerifies); Interlocked.Increment(ref _affected); continue; }
                    if (fault == "client-flight-loss-reorder")
                    {
                        if (fromLeft && fragment && bytes[13] == 11 && _droppedClientCertificates == 0)
                        { Interlocked.Increment(ref _droppedClientCertificates); Interlocked.Increment(ref _affected); continue; }
                        if (fromLeft && plainHandshake && bytes[13] == 16 && _reorderedClientKeys == 0)
                        { heldClientKey = bytes; continue; }
                        if (fromLeft && plainHandshake && bytes[13] == 15 && heldClientKey != null)
                        {
                            await _socket.SendToAsync(bytes, SocketFlags.None, destination, _lifetime.Token);
                            await _socket.SendToAsync(heldClientKey, SocketFlags.None, destination, _lifetime.Token);
                            heldClientKey = null; Interlocked.Increment(ref _reorderedClientKeys); Interlocked.Increment(ref _affected); continue;
                        }
                        if (fromLeft && bytes[0] == 20 && _reorderedClientCcs == 0)
                        { heldClientCcs = bytes; continue; }
                        if (fromLeft && bytes.Length >= 13 && bytes[0] == 22 && bytes[4] == 1 && heldClientCcs != null)
                        {
                            await _socket.SendToAsync(bytes, SocketFlags.None, destination, _lifetime.Token);
                            await _socket.SendToAsync(heldClientCcs, SocketFlags.None, destination, _lifetime.Token);
                            heldClientCcs = null; Interlocked.Increment(ref _reorderedClientCcs); Interlocked.Increment(ref _affected); continue;
                        }
                        if (!fromLeft && ContainsEncryptedHandshake(bytes) && _droppedFinished == 0)
                        { Interlocked.Increment(ref _droppedFinished); Interlocked.Increment(ref _affected); continue; }
                    }
                    if (fault == "combined-loss-reorder")
                    {
                        if (fromLeft && plainHandshake && bytes[13] == 1 && _droppedHellos < 2)
                        { Interlocked.Increment(ref _droppedHellos); Interlocked.Increment(ref _affected); continue; }
                        if (!fromLeft && fragment && bytes[13] == 11 && _droppedCertificates == 0)
                        { Interlocked.Increment(ref _droppedCertificates); Interlocked.Increment(ref _affected); continue; }
                        if (!fromLeft && ContainsEncryptedHandshake(bytes) && _droppedFinished == 0)
                        { Interlocked.Increment(ref _droppedFinished); Interlocked.Increment(ref _affected); continue; }
                        if (!fromLeft && plainHandshake && _reorderedFlights == 0)
                        {
                            if (bytes[13] == 2 && held == null) { held = bytes; continue; }
                            if (held != null)
                            {
                                await _socket.SendToAsync(bytes, SocketFlags.None, destination, _lifetime.Token);
                                await _socket.SendToAsync(held, SocketFlags.None, destination, _lifetime.Token);
                                held = null; Interlocked.Increment(ref _reorderedFlights); Interlocked.Increment(ref _affected); continue;
                            }
                        }
                    }
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
    private static bool ContainsEncryptedHandshake(ReadOnlySpan<byte> bytes)
    {
        for (var offset = 0; offset + 13 <= bytes.Length;)
        {
            var length = 13 + BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 11)..]);
            if (length > bytes.Length - offset) return false;
            if (bytes[offset] == 22 && BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]) == 1) return true;
            offset += length;
        }
        return false;
    }
    public async ValueTask DisposeAsync()
    { _lifetime.Cancel(); await _worker; _socket.Dispose(); _lifetime.Dispose(); }
}

internal sealed record DtlsOffer(bool Controlling, bool Secure, bool DtlsClient, string Fingerprint, ushort Profile, int Mtu,
    string Fragment, string Password, string Candidate, int MediaPackets = 0);
internal sealed record DtlsDescription(string Fingerprint, string Fragment, string Password, string Address, int Port, uint Priority);
