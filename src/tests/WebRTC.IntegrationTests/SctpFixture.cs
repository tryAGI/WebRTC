using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal sealed class SctpPair : IAsyncDisposable
{
    private readonly IceUdpTransport _leftIce, _rightIce;
    private readonly DtlsIdentity _leftIdentity, _rightIdentity;
    public readonly DtlsSrtpTransport ClientDtls, ServerDtls;
    public readonly SctpAssociation Left, Right;
    public readonly SctpProxy Proxy;
    private SctpPair(SctpOptions options, SctpOptions? remoteOptions, SctpRole rightRole)
    {
        var iceOptions = new IceUdpTransportOptions { CheckInterval = TimeSpan.FromMilliseconds(10), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100) };
        _leftIce = new(new(IPAddress.Loopback, 0), options: iceOptions); _rightIce = new(new(IPAddress.Loopback, 0), options: iceOptions);
        _leftIdentity = DtlsIdentity.Generate(); _rightIdentity = DtlsIdentity.Generate();
        var dtlsOptions = new DtlsSrtpOptions { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100) };
        ClientDtls = new(_leftIce, _leftIdentity, DtlsRole.Client, _rightIdentity.GetFingerprintSha256(), dtlsOptions);
        ServerDtls = new(_rightIce, _rightIdentity, DtlsRole.Server, _leftIdentity.GetFingerprintSha256(), dtlsOptions);
        Left = new(ClientDtls, SctpRole.Initiator, options); Right = new(ServerDtls, rightRole, remoteOptions ?? options);
        Proxy = new(_leftIce.LocalEndPoint, _rightIce.LocalEndPoint);
    }
    internal static async Task<SctpPair> Create(SctpOptions options, CancellationToken ct, int initialDrops = 0, bool connectSctp = true, SctpOptions? remoteOptions = null, SctpRole rightRole = SctpRole.Responder)
    {
        var pair = new SctpPair(options, remoteOptions, rightRole);
        try
        {
            await Task.WhenAll(pair._leftIce.ConnectAsync(pair._rightIce.LocalCredentials, IceRole.Controlling, [new(pair.Proxy.EndPoint)], ct),
                pair._rightIce.ConnectAsync(pair._leftIce.LocalCredentials, IceRole.Controlled, [new(pair.Proxy.EndPoint)], ct));
            await Task.WhenAll(pair.ClientDtls.ConnectAsync(ct), pair.ServerDtls.ConnectAsync(ct));
            pair.Proxy.DropLeftApplications = initialDrops;
            if (connectSctp) await Task.WhenAll(pair.Left.ConnectAsync(ct), pair.Right.ConnectAsync(ct));
            return pair;
        }
        catch { await pair.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await Left.DisposeAsync(); await Right.DisposeAsync(); await ClientDtls.DisposeAsync(); await ServerDtls.DisposeAsync();
        await Proxy.DisposeAsync(); await _leftIce.DisposeAsync(); await _rightIce.DisposeAsync(); _leftIdentity.Dispose(); _rightIdentity.Dispose();
    }
}
internal sealed class SctpProxy : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _leftDrops, _rightDrops, _dropped;
    public int DropLeftApplications { set => Volatile.Write(ref _leftDrops, value); }
    public int DropRightApplications { set => Volatile.Write(ref _rightDrops, value); }
    public int Dropped => Volatile.Read(ref _dropped);
    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    internal SctpProxy(IPEndPoint left, IPEndPoint right)
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _worker = Task.Run(async () =>
        {
            var bytes = new byte[2048];
            try
            {
                while (true)
                {
                    var result = await _socket.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                    var fromLeft = result.RemoteEndPoint.Equals(left);
                    if (!fromLeft && !result.RemoteEndPoint.Equals(right)) continue;
                    if (fromLeft && bytes[0] == 23 && Volatile.Read(ref _leftDrops) > 0)
                    { Interlocked.Decrement(ref _leftDrops); Interlocked.Increment(ref _dropped); continue; }
                    if (!fromLeft && bytes[0] == 23 && Volatile.Read(ref _rightDrops) > 0)
                    { Interlocked.Decrement(ref _rightDrops); Interlocked.Increment(ref _dropped); continue; }
                    await _socket.SendToAsync(bytes.AsMemory(0, result.ReceivedBytes), SocketFlags.None, fromLeft ? right : left, _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    public async ValueTask DisposeAsync() { _lifetime.Cancel(); await _worker; _socket.Dispose(); _lifetime.Dispose(); }
}
