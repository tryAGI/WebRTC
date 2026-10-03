using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using tryAGI.WebRTC;

// Authored adversarial stream bridge to our synthetic UDP responder. Pion is the independent positive gate.
internal sealed class TurnStreamFixture : IAsyncDisposable
{
    internal readonly TurnFixture Backend = new(modern: true);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(25));
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Socket _upstream = TurnFixture.Socket(IPAddress.Loopback);
    private readonly X509Certificate2? _certificate;
    private readonly byte[]? _root;
    private readonly Task _run;
    private TcpClient? _client;
    private Stream? _stream;
    internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Fragment, AllowTlsRejection, StallHandshake;
    internal volatile bool HoldReads;
    internal readonly TaskCompletionSource ReadPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int FramesRead;
    internal TurnStreamFixture(bool tls = false, bool expired = false, bool wrongPurpose = false)
    {
        if (tls) (_certificate, _root) = Certificate(expired, wrongPurpose);
        _listener.Server.ReceiveBufferSize = 4096; _listener.Start(); _run = Run();
    }
    internal IPEndPoint Server => (IPEndPoint)_listener.LocalEndpoint;
    internal TurnUdpOptions Options => TurnFixture.Fast() with
    {
        ServerTransport = _certificate == null ? TurnServerTransport.Tcp : TurnServerTransport.Tls,
        Tls = _certificate == null ? null : new() { ServerName = "turn.fixture.local", RevocationMode = X509RevocationMode.NoCheck, TrustedRootCertificates = [_root!] },
    };
    private static (X509Certificate2, byte[]) Certificate(bool expired, bool wrongPurpose)
    {
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuer = new CertificateRequest("CN=Isolated TURN root", issuerKey, HashAlgorithmName.SHA256);
        issuer.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        issuer.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuer.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var root = issuer.CreateSelfSigned(now.AddHours(-2), now.AddHours(2));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leaf = new CertificateRequest("CN=turn.fixture.local", key, HashAlgorithmName.SHA256);
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var purposes = new OidCollection { new(wrongPurpose ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") };
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("turn.fixture.local"); leaf.CertificateExtensions.Add(san.Build());
        using var publicLeaf = leaf.Create(root, now.AddHours(-1), expired ? now.AddMinutes(-1) : now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        var certificate = publicLeaf.CopyWithPrivateKey(key);
        if (!OperatingSystem.IsWindows()) return (certificate, root.RawData);
        // Schannel needs a key-container handle. Import the synthetic PFX from memory,
        // without PersistKeySet; certificate disposal owns the temporary Windows key.
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        try { return (X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet), root.RawData); }
        finally { certificate.Dispose(); CryptographicOperations.ZeroMemory(pfx); }
    }
    private async Task Run()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_lifetime.Token); _client.NoDelay = true; _client.ReceiveBufferSize = 4096;
            _stream = _client.GetStream();
            if (_certificate != null)
            {
                if (StallHandshake) { await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token); return; }
                var ssl = new SslStream(_stream, false); _stream = ssl;
                await ssl.AuthenticateAsServerAsync(new() { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _lifetime.Token);
            }
            Ready.TrySetResult();
            var upload = Upload(); var download = Download();
            var finished = await Task.WhenAny(upload, download);
            var failure = finished.Exception;
            _lifetime.Cancel(); _client.Close(); _upstream.Dispose();
            try { await Task.WhenAll(upload, download); } catch (Exception) when (_lifetime.IsCancellationRequested) { }
            if (failure != null) throw new InvalidOperationException("Synthetic TURN stream worker failed", failure);
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or IOException or SocketException) { }
        catch (Exception error) when (AllowTlsRejection && error is AuthenticationException or IOException) { }
        finally { Ready.TrySetCanceled(); _client?.Close(); }
    }
    private async Task Upload()
    {
        try
        {
            var frame = new byte[20480];
            while (true)
            {
                if (HoldReads) { ReadPaused.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token); return; }
                await _stream!.ReadExactlyAsync(frame.AsMemory(0, 4), _lifetime.Token);
                if (HoldReads) { ReadPaused.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token); return; }
                var length = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
                var channel = (frame[0] & 0xC0) == 0x40;
                var size = channel ? 4 + ((length + 3) / 4 * 4) : 20 + length;
                TurnFixture.Check(size <= frame.Length && (channel || (length & 3) == 0));
                await _stream.ReadExactlyAsync(frame.AsMemory(4, size - 4), _lifetime.Token);
                Interlocked.Increment(ref FramesRead);
                await _upstream.SendToAsync(frame.AsMemory(0, channel ? 4 + length : size), SocketFlags.None, Backend.Server, _lifetime.Token);
            }
        }
        catch (EndOfStreamException) { }
        catch (IOException) when (_client?.Connected == false || _lifetime.IsCancellationRequested) { }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task Download()
    {
        try
        {
            var bytes = new byte[20480];
            while (true)
            {
                var received = await _upstream.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                TurnFixture.Check(received.RemoteEndPoint.Equals(Backend.Server));
                var length = received.ReceivedBytes;
                if (bytes[0] is >= 0x40 and <= 0x4F)
                { var padded = (length + 3) & ~3; bytes.AsSpan(length, padded - length).Clear(); length = padded; }
                await Inject(bytes.AsMemory(0, length), _lifetime.Token);
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }
    internal async Task Inject(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await Ready.Task.WaitAsync(ct); await _writes.WaitAsync(ct);
        try
        {
            if (!Fragment) await _stream!.WriteAsync(bytes, ct);
            else for (var offset = 0; offset < bytes.Length; offset += 3)
            { await _stream!.WriteAsync(bytes.Slice(offset, Math.Min(3, bytes.Length - offset)), ct); await Task.Delay(1, ct); }
        }
        finally { _writes.Release(); }
    }
    internal void EndStream() => _client!.Client.Shutdown(SocketShutdown.Send);
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _listener.Stop(); _client?.Dispose(); _upstream.Dispose();
        try { await _run; }
        finally { if (_stream != null) await _stream.DisposeAsync(); await Backend.DisposeAsync(); _certificate?.Dispose(); _lifetime.Dispose(); }
    }
}
