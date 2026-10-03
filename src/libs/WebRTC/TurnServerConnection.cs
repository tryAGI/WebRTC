using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace tryAGI.WebRTC;

public enum TurnServerTransport { Udp, Tcp, Tls }

/// <summary>Explicit TLS identity/trust policy. No certificate-validation override callback.</summary>
public sealed record TurnTlsOptions
{
    public required string ServerName { get; init; }
    /// <summary>Empty uses system roots. Otherwise only these DER roots are trusted; at most eight, 16 KiB each.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> TrustedRootCertificates { get; init; } = [];
    /// <summary>Online may contact certificate revocation services. Isolated test roots explicitly use NoCheck.</summary>
    public X509RevocationMode RevocationMode { get; init; } = X509RevocationMode.Online;
}

// One socket/stream owner, shared by media and authenticated control. Auth/lifetimes remain in the allocation.
internal sealed class TurnServerConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _server;
    private Stream? _stream;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private int _stopped, _writers;
    private readonly object _writerGate = new();
    private TaskCompletionSource? _writersDrained;
    private readonly TimeSpan _writeTimeout;
    internal bool IsStream { get; }
    internal IPEndPoint LocalEndPoint => new IceCandidate((IPEndPoint)_socket.LocalEndPoint!).EndPoint;
    private TurnServerConnection(IPEndPoint local, IPEndPoint server, bool stream, TimeSpan writeTimeout)
    {
        _server = new IceCandidate(server).EndPoint;
        _ = new IceCandidate(new(local.Address, local.Port == 0 ? 1 : local.Port));
        if (local.AddressFamily != server.AddressFamily) throw new ArgumentException("TURN server and local base families must match.");
        IsStream = stream; _writeTimeout = writeTimeout;
        _socket = new(local.AddressFamily, stream ? SocketType.Stream : SocketType.Dgram, stream ? ProtocolType.Tcp : ProtocolType.Udp);
        try { _socket.Bind(new IPEndPoint(local.Address, local.Port)); if (stream) _socket.NoDelay = true; }
        catch { _socket.Dispose(); throw; }
    }
    internal static async Task<TurnServerConnection> OpenAsync(IPEndPoint local, IPEndPoint server, TurnUdpOptions options, CancellationToken ct)
    {
        // Snapshot caller trust data and validate it before the first network await.
        var roots = new List<X509Certificate2>();
        TurnServerConnection? connection = null;
        try
        {
            SslClientAuthenticationOptions? authentication = null;
            if (options.Tls is { } tls)
            {
                if (string.IsNullOrEmpty(tls.ServerName) || tls.ServerName.Length > 253 || Uri.CheckHostName(tls.ServerName) == UriHostNameType.Unknown ||
                    !Enum.IsDefined(tls.RevocationMode) || tls.TrustedRootCertificates == null || tls.TrustedRootCertificates.Count > 8)
                    throw new ArgumentException("Invalid explicit TURN TLS identity/trust policy.");
                var policy = new X509ChainPolicy
                {
                    RevocationMode = tls.RevocationMode, VerificationFlags = X509VerificationFlags.NoFlag,
                    DisableCertificateDownloads = true, UrlRetrievalTimeout = TimeSpan.FromSeconds(1),
                    TrustMode = tls.TrustedRootCertificates.Count == 0 ? X509ChainTrustMode.System : X509ChainTrustMode.CustomRootTrust,
                };
                policy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
                foreach (var bytes in tls.TrustedRootCertificates)
                {
                    if (bytes.Length is < 1 or > 16384) throw new ArgumentException("TURN TLS root certificate exceeds its DER budget.");
                    var root = X509CertificateLoader.LoadCertificate(bytes.Span); roots.Add(root); policy.CustomTrustStore.Add(root);
                }
                authentication = new()
                {
                    TargetHost = tls.ServerName, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateChainPolicy = policy, AllowRenegotiation = false,
                };
            }
            connection = new(local, server, options.ServerTransport != TurnServerTransport.Udp, options.StreamWriteTimeout);
            if (connection.IsStream)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(options.ConnectTimeout);
                try
                {
                    await connection._socket.ConnectAsync(connection._server, deadline.Token).ConfigureAwait(false);
                    var stream = new NetworkStream(connection._socket, ownsSocket: false); connection._stream = stream;
                    if (authentication != null)
                    {
                        var ssl = new SslStream(stream, leaveInnerStreamOpen: false); connection._stream = ssl;
                        await ssl.AuthenticateAsClientAsync(authentication, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new TimeoutException("TURN TCP/TLS connection deadline expired."); }
            }
            ct.ThrowIfCancellationRequested(); return connection;
        }
        catch { if (connection != null) await connection.DisposeAsync().ConfigureAwait(false); throw; }
        finally { foreach (var root in roots) root.Dispose(); }
    }
    internal async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct, bool control = false)
    {
        if (!IsStream) { await _socket.SendToAsync(packet, SocketFlags.None, _server, ct).ConfigureAwait(false); return; }
        lock (_writerGate)
        {
            if (Volatile.Read(ref _stopped) != 0) throw new ObjectDisposedException(nameof(TurnServerConnection));
            if (_writers >= (control ? 65 : 64)) throw new InvalidOperationException("TURN stream writer admission budget exhausted.");
            if (_writers++ == 0) _writersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        cancel.CancelAfter(_writeTimeout);
        var entered = false;
        try
        {
            await _writing.WaitAsync(cancel.Token).ConfigureAwait(false); entered = true;
            cancel.Token.ThrowIfCancellationRequested();
            try { await _stream!.WriteAsync(packet, cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_closed.IsCancellationRequested)
            { Stop(); throw new TimeoutException("TURN stream write completion deadline expired."); }
            catch { Stop(); throw; } // A canceled/failed write might have emitted a prefix. Never reuse that stream.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_closed.IsCancellationRequested)
        { throw new TimeoutException("TURN stream write admission deadline expired."); }
        finally
        {
            if (entered) _writing.Release();
            lock (_writerGate) if (--_writers == 0) _writersDrained!.TrySetResult();
        }
    }
    internal async ValueTask<SocketReceiveFromResult> ReceiveAsync(Memory<byte> buffer, int maximumDatagram, CancellationToken ct)
    {
        if (!IsStream)
            return await _socket.ReceiveFromAsync(buffer, SocketFlags.None,
                new IPEndPoint(_server.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), ct).ConfigureAwait(false);
        await _stream!.ReadExactlyAsync(buffer[..4], ct).ConfigureAwait(false);
        var kind = buffer.Span[0]; var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Span[2..]);
        int size;
        if (kind is >= 0x40 and <= 0x4F)
        {
            if (length > maximumDatagram) throw new InvalidDataException("TURN stream ChannelData exceeds the inner datagram budget.");
            size = (4 + length + 3) & ~3;
        }
        else if (kind <= 3)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(buffer.Span); size = 20 + length;
            if ((length & 3) != 0 || size > buffer.Length || type != 0x0017 && size > 4096)
                throw new InvalidDataException("TURN stream STUN frame exceeds the supported framing budget.");
            await _stream.ReadExactlyAsync(buffer.Slice(4, 16), ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(buffer.Span[4..]) != 0x2112A442)
                throw new InvalidDataException("TURN stream STUN magic cookie is invalid.");
            await _stream.ReadExactlyAsync(buffer.Slice(20, length), ct).ConfigureAwait(false);
            return new() { ReceivedBytes = size, RemoteEndPoint = _server };
        }
        else throw new InvalidDataException("Unsupported TURN stream frame prefix.");
        await _stream.ReadExactlyAsync(buffer.Slice(4, size - 4), ct).ConfigureAwait(false);
        return new() { ReceivedBytes = size, RemoteEndPoint = _server };
    }
    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _closed.Cancel(); _socket.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        Stop();
        Task drained; lock (_writerGate) drained = _writersDrained?.Task ?? Task.CompletedTask;
        await drained.ConfigureAwait(false);
        if (_stream != null) await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
