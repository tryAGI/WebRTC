using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using tryAGI.WebRTC;

var cases = new List<(string Name, Func<Task> Run)>
{
    ("IPv4 nominated pair exchanges real UDP datagrams", () => Exchange(IPAddress.Loopback, IceRole.Controlling, IceRole.Controlled)),
    ("IPv6 nominated pair exchanges real UDP datagrams", () => Exchange(IPAddress.IPv6Loopback, IceRole.Controlling, IceRole.Controlled)),
    ("Both-controlling role conflict converges", () => Exchange(IPAddress.Loopback, IceRole.Controlling, IceRole.Controlling)),
    ("Both-controlled role conflict converges", () => Exchange(IPAddress.Loopback, IceRole.Controlled, IceRole.Controlled)),
    ("Trickle candidates connect without gathering barrier", Trickle),
    ("STUN retransmission survives dropped network packets", PacketLoss),
    ("Silent preferred candidate does not block working pair", () => CandidateFallback(false)),
    ("Unreachable UDP candidate does not abort the checklist", () => CandidateFallback(true)),
    ("Wrong credentials cannot nominate a pair", WrongCredentials),
    ("Unknown UDP source cannot deliver data", UnknownSource),
    ("Consent loss stops outbound traffic", ConsentLoss),
    ("Cancellation closes an incomplete connection", Cancellation),
    ("Candidate pair limits bound trickle input", CandidateLimits),
    ("Bounded receive queue drops stale backlog", ReceiveBackpressure),
    ("Early requests require authentication and bound buffered state", EarlyCheckBounds),
    ("Early request remote identity is checked after signaling", EarlyIdentity),
};
foreach (var profile in Enum.GetValues<SrtpProfile>())
    cases.Add(($"Encrypted ICE datagrams: {profile}", () => SrtpInterop.Network(profile)));

if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--pion-uri" || !Uri.TryCreate(args[1], UriKind.Absolute, out var pionUri) ||
        pionUri.Scheme != "http" || pionUri.UserInfo.Length != 0 ||
        !(pionUri.Host == "localhost" || (IPAddress.TryParse(pionUri.Host, out var address) && IPAddress.IsLoopback(address))))
        throw new ArgumentException("The independent peer must be an explicit local HTTP endpoint.");
    cases.Add(("Pion controlled peer interoperability", () => Pion(pionUri, IceRole.Controlling)));
    cases.Add(("Pion controlling peer interoperability", () => Pion(pionUri, IceRole.Controlled)));
    cases.Add(("Pion early authenticated checks survive signaling delay", () => Pion(pionUri, IceRole.Controlled, waitForEarlyCheck: true)));
    foreach (var profile in Enum.GetValues<SrtpProfile>())
        cases.Add(($"Pion SRTP/SRTCP encryption, decryption and E=0: {profile}", () => SrtpInterop.Pion(pionUri, profile)));
}

var failed = 0;
foreach (var (name, run) in cases)
{
    var timer = Stopwatch.StartNew();
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalMilliseconds:F1} ms)");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}
Console.WriteLine($"{cases.Count - failed}/{cases.Count} network integration cases passed");
return failed == 0 ? 0 : 1;

static IceUdpTransportOptions Fast() => new()
{
    CheckInterval = TimeSpan.FromMilliseconds(10),
    InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100),
    ConnectionTimeout = TimeSpan.FromSeconds(4),
};

static void Check(bool value, string message = "Assertion failed")
{
    if (!value) throw new InvalidOperationException(message);
}

static async Task ConnectPair(IceUdpTransport left, IceUdpTransport right, IceRole leftRole = IceRole.Controlling, IceRole rightRole = IceRole.Controlled)
{
    await Task.WhenAll(
        left.ConnectAsync(right.LocalCredentials, leftRole, [new(right.LocalEndPoint)]),
        right.ConnectAsync(left.LocalCredentials, rightRole, [new(left.LocalEndPoint)]));
}

static async Task<byte[]> Read(IceUdpTransport transport, TimeSpan? timeout = null)
{
    using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
    await foreach (var bytes in transport.ReceiveDatagramsAsync(deadline.Token)) return bytes;
    throw new IOException("Transport ended before receiving a datagram.");
}

static async Task Exchange(IPAddress address, IceRole leftRole, IceRole rightRole)
{
    await using var left = new IceUdpTransport(new(address, 0), options: Fast());
    await using var right = new IceUdpTransport(new(address, 0), options: Fast());
    await ConnectPair(left, right, leftRole, rightRole);
    Check(left.GetDiagnostics().Role != right.GetDiagnostics().Role);
    if (leftRole == rightRole) Check(left.GetDiagnostics().RoleConflicts + right.GetDiagnostics().RoleConflicts > 0);
    await left.SendDatagramAsync("left-to-right"u8.ToArray());
    await right.SendDatagramAsync("right-to-left"u8.ToArray());
    Check((await Read(right)).AsSpan().SequenceEqual("left-to-right"u8));
    Check((await Read(left)).AsSpan().SequenceEqual("right-to-left"u8));
    Check(left.GetDiagnostics().ConnectionTime is { } && left.GetDiagnostics().LastCheckRoundTripTime is { });
    Console.WriteLine($"  nominated pair: left={left.GetDiagnostics().ConnectionTime!.Value.TotalMilliseconds:F1} ms, right={right.GetDiagnostics().ConnectionTime!.Value.TotalMilliseconds:F1} ms");
}

static async Task Trickle()
{
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    var leftConnection = left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, []);
    var rightConnection = right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, []);
    await Task.Delay(30);
    Check(!leftConnection.IsCompleted && !rightConnection.IsCompleted);
    // Only one side is signaled: the other must learn an authenticated peer-reflexive candidate.
    left.AddRemoteCandidate(new(right.LocalEndPoint));
    await Task.WhenAll(leftConnection, rightConnection);
    Check(right.GetDiagnostics().CandidatePairs == 1);
}

static async Task PacketLoss()
{
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    await using var proxy = new LossProxy(left.LocalEndPoint, right.LocalEndPoint, drops: 2);
    await Task.WhenAll(
        left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(proxy.EndPoint)]),
        right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(proxy.EndPoint)]));
    Check(proxy.Dropped == 2);
    Check(left.GetDiagnostics().Retransmissions + right.GetDiagnostics().Retransmissions >= 2);
    await left.SendDatagramAsync("loss-recovery"u8.ToArray());
    Check((await Read(right)).AsSpan().SequenceEqual("loss-recovery"u8));
}

static async Task CandidateFallback(bool unreachable)
{
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    using var silent = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var unavailableEndpoint = (IPEndPoint)silent.LocalEndPoint!;
    if (unreachable) silent.Dispose();
    await Task.WhenAll(
        left.ConnectAsync(right.LocalCredentials, IceRole.Controlling,
            [new(unavailableEndpoint, int.MaxValue), new(right.LocalEndPoint)]),
        right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)]));
    Check(left.GetDiagnostics().SelectedRemoteEndPoint!.Equals(right.LocalEndPoint));
}

static async Task WrongCredentials()
{
    var options = Fast() with { ConnectionTimeout = TimeSpan.FromMilliseconds(450) };
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: options);
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: options);
    var falseCredentials = new IceCredentials(right.LocalCredentials.UsernameFragment, IceCredentials.Generate().Password);
    var connections = Task.WhenAll(
        left.ConnectAsync(falseCredentials, IceRole.Controlling, [new(right.LocalEndPoint)]),
        right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)]));
    try { await connections; Check(false, "Invalid credentials connected."); }
    catch (TimeoutException) { }
    Check(left.GetDiagnostics().SelectedRemoteEndPoint is null && right.GetDiagnostics().SelectedRemoteEndPoint is null);
}

static async Task UnknownSource()
{
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    await ConnectPair(left, right);
    using var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    await attacker.SendToAsync("unknown-source"u8.ToArray(), SocketFlags.None, right.LocalEndPoint);
    await left.SendDatagramAsync("selected-source"u8.ToArray());
    Check((await Read(right)).AsSpan().SequenceEqual("selected-source"u8));
    await Task.Delay(30);
    Check(right.GetDiagnostics().DroppedDatagrams >= 1);
}

static async Task ConsentLoss()
{
    var options = Fast() with { ConsentInterval = TimeSpan.FromMilliseconds(100), ConsentTimeout = TimeSpan.FromMilliseconds(350) };
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: options);
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: options);
    await ConnectPair(left, right);
    // Prove fresh consent is actually renewed before removing the peer.
    await Task.Delay(500);
    Check(!left.Completion.IsCompleted && !right.Completion.IsCompleted);
    await right.DisposeAsync();
    Check(await left.Completion.WaitAsync(TimeSpan.FromSeconds(2)) is IOException);
    try { await left.SendDatagramAsync("must-not-send"u8.ToArray()); Check(false); }
    catch (IOException) { }
}

static async Task Cancellation()
{
    await using var transport = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    try { await transport.ConnectAsync(IceCredentials.Generate(), IceRole.Controlling, [], deadline.Token); Check(false); }
    catch (OperationCanceledException) { }
    Check(await transport.Completion is OperationCanceledException);
}

static async Task CandidateLimits()
{
    await using var transport = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast() with { MaximumCandidatePairs = 2 });
    using var deadline = new CancellationTokenSource();
    var connection = transport.ConnectAsync(IceCredentials.Generate(), IceRole.Controlling, [], deadline.Token);
    transport.AddRemoteCandidate(new(new(IPAddress.Loopback, 20001)));
    transport.AddRemoteCandidate(new(new(IPAddress.Loopback, 20002)));
    var rejected = false;
    try { transport.AddRemoteCandidate(new(new(IPAddress.Loopback, 20003))); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected);
    Check(transport.GetDiagnostics().CandidatePairs == 2);
    deadline.Cancel();
    try { await connection; Check(false); }
    catch (OperationCanceledException) { }
}

static async Task ReceiveBackpressure()
{
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast() with { ReceiveQueueCapacity = 2 });
    await ConnectPair(left, right);
    for (var i = 0; i < 30; i++) await left.SendDatagramAsync(new byte[] { 0x80, (byte)i });
    var deadline = Stopwatch.StartNew();
    while (right.GetDiagnostics().DroppedDatagrams < 28 && deadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
    Check(right.GetDiagnostics().DroppedDatagrams == 28);
    Check((await Read(right))[1] == 28 && (await Read(right))[1] == 29);
}

static async Task Pion(Uri uri, IceRole role, bool waitForEarlyCheck = false)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    for (var attempt = 0; ; attempt++)
    {
        try { using var ready = await http.GetAsync(new Uri(uri, "/health"), deadline.Token); ready.EnsureSuccessStatusCode(); break; }
        catch (HttpRequestException) when (attempt < 20) { await Task.Delay(50, deadline.Token); }
    }
    await using var transport = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    var endpoint = transport.LocalEndPoint;
    var offer = new PeerOffer(role == IceRole.Controlled, transport.LocalCredentials.UsernameFragment,
        transport.LocalCredentials.Password, $"1 1 udp 2130706431 {endpoint.Address} {endpoint.Port} typ host");
    using var response = await http.PostAsJsonAsync(new Uri(uri, "/peer"), offer, InteropJson.Default.PeerOffer, deadline.Token);
    response.EnsureSuccessStatusCode();
    var remote = await response.Content.ReadFromJsonAsync(InteropJson.Default.PeerResponse, deadline.Token)
        ?? throw new IOException("Independent peer returned no description.");
    Check(IPAddress.TryParse(remote.Address, out var ip) && IPAddress.IsLoopback(ip));
    if (waitForEarlyCheck)
    {
        var wait = Stopwatch.StartNew();
        while (transport.GetDiagnostics().BufferedEarlyChecks == 0 && wait.Elapsed < TimeSpan.FromSeconds(1))
            await Task.Delay(5, deadline.Token);
        Check(transport.GetDiagnostics().BufferedEarlyChecks > 0, "No early authenticated request was exercised.");
        await Task.Delay(100, deadline.Token);
        Check(!transport.IsConnected, "Early credentials must not establish connectivity before remote signaling.");
    }
    await transport.ConnectAsync(new(remote.Fragment, remote.Password), role, [new(new(ip!, remote.Port), remote.Priority)], deadline.Token);
    if (waitForEarlyCheck) Check(transport.GetDiagnostics().ConnectionTime < TimeSpan.FromMilliseconds(200), "Early check waited for the peer retransmission timer.");
    await transport.SendDatagramAsync("independent-peer"u8.ToArray(), deadline.Token);
    Check((await Read(transport)).AsSpan().SequenceEqual("pion:independent-peer"u8));
    Console.WriteLine($"  Pion nomination={transport.GetDiagnostics().ConnectionTime!.Value.TotalMilliseconds:F1} ms, check RTT={transport.GetDiagnostics().LastCheckRoundTripTime!.Value.TotalMilliseconds:F1} ms");
}

static async Task SendEarlyRequest(Socket source, IceUdpTransport destination, string remoteFragment, string key)
{
    var buffer = new byte[1024];
    var writer = new StunMessageWriter(buffer, 1, System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    Check(writer.TryAddAttribute(6, Encoding.ASCII.GetBytes(destination.LocalCredentials.UsernameFragment + ":" + remoteFragment)));
    Check(writer.TryAddUInt32(0x24, 1862270975) && writer.TryAddUInt64(0x802A, 42));
    Check(writer.TryComplete(Encoding.ASCII.GetBytes(key), true, out var length));
    await source.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, destination.LocalEndPoint);
}

static async Task EarlyCheckBounds()
{
    await using var transport = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    using var source = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var remote = IceCredentials.Generate();
    await SendEarlyRequest(source, transport, remote.UsernameFragment, remote.Password);
    await Task.Delay(30);
    Check(transport.GetDiagnostics().BufferedEarlyChecks == 0);
    for (var i = 0; i < 24; i++) await SendEarlyRequest(source, transport, remote.UsernameFragment, transport.LocalCredentials.Password);
    var timer = Stopwatch.StartNew();
    while (transport.GetDiagnostics().BufferedEarlyChecks != 16 && timer.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(5);
    Check(transport.GetDiagnostics().BufferedEarlyChecks == 16);
    Check(!transport.IsConnected && transport.GetDiagnostics().ValidatedRequests == 0);
    await Task.Delay(2200);
    Check(transport.GetDiagnostics().BufferedEarlyChecks == 0, "Expired early requests retained state.");
}

static async Task EarlyIdentity()
{
    await using var transport = new IceUdpTransport(new(IPAddress.Loopback, 0), options: Fast());
    using var source = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    await SendEarlyRequest(source, transport, IceCredentials.Generate().UsernameFragment, transport.LocalCredentials.Password);
    var timer = Stopwatch.StartNew();
    while (transport.GetDiagnostics().BufferedEarlyChecks == 0 && timer.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(5);
    Check(transport.GetDiagnostics().BufferedEarlyChecks == 1);
    using var deadline = new CancellationTokenSource();
    var connection = transport.ConnectAsync(IceCredentials.Generate(), IceRole.Controlled, [], deadline.Token);
    Check(transport.GetDiagnostics().CandidatePairs == 0 && transport.GetDiagnostics().ValidatedRequests == 0);
    Check(transport.GetDiagnostics().BufferedEarlyChecks == 0 && !transport.IsConnected);
    deadline.Cancel();
    try { await connection; Check(false); }
    catch (OperationCanceledException) { }
}

internal sealed record PeerOffer(bool Controlling, string Fragment, string Password, string Candidate);
internal sealed record PeerResponse(string Fragment, string Password, string Address, int Port, uint Priority);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PeerOffer))]
[JsonSerializable(typeof(PeerResponse))]
internal partial class InteropJson : JsonSerializerContext;

internal sealed class LossProxy : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _dropped;
    public int Dropped => Volatile.Read(ref _dropped);
    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public LossProxy(IPEndPoint left, IPEndPoint right, int drops)
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _worker = Task.Run(async () =>
        {
            var buffer = new byte[2048];
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                    if (!received.RemoteEndPoint.Equals(left) && !received.RemoteEndPoint.Equals(right)) continue;
                    if (buffer[0] == 0 && buffer[1] == 1 && _dropped < drops) { Interlocked.Increment(ref _dropped); continue; }
                    await _socket.SendToAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None,
                        received.RemoteEndPoint.Equals(left) ? right : left, _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _worker;
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
