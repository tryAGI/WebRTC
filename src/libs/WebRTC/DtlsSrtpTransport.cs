using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public enum DtlsRole { Client, Server }
public enum SecureMediaKind { Rtp, Rtcp }
public sealed record SecureMediaDatagram(SecureMediaKind Kind, byte[] Data);
public sealed record DtlsSrtpOptions
{
    public bool RequireCookie { get; init; }
    public int MaximumDatagramSize { get; init; } = 1200;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan InitialRetransmissionTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public IReadOnlyList<SrtpProfile> Profiles { get; init; } =
        [SrtpProfile.AeadAes128Gcm, SrtpProfile.AeadAes256Gcm, SrtpProfile.Aes128CmHmacSha1_80];
}
public sealed record DtlsSrtpDiagnostics(DtlsRole Role, SrtpProfile? Profile, TimeSpan? HandshakeTime,
    long Retransmissions, long RejectedRecords, long DroppedApplicationDatagrams, long DroppedMediaDatagrams);

/// <summary>
/// Mutually fingerprint-bound DTLS 1.2/SRTP over one nominated ICE component.
/// Owns its cryptographic state, consumes the ICE receive stream, and leaves ICE/identity disposal to their owners.
/// This is not SDP, RTP codec assembly or SCTP/data-channel negotiation.
/// </summary>
public sealed class DtlsSrtpTransport : IAsyncDisposable
{
    private const ushort EcdsaCipher = 0xC02B, RsaCipher = 0xC02F;
    private readonly IceUdpTransport _ice;
    private readonly DtlsIdentity _identity;
    private readonly DtlsRole _role;
    private readonly DtlsSrtpOptions _options;
    private readonly SrtpProfile[] _profiles;
    private readonly byte[] _remoteFingerprint;
    private readonly byte[] _cookieKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _localRandom = RandomNumberGenerator.GetBytes(32);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _application;
    private readonly Channel<SecureMediaDatagram> _media;
    private readonly IncrementalHash _transcript;
    private readonly Dictionary<ushort, Assembly> _assemblies = [];
    private readonly List<byte[]> _pendingEncrypted = [];
    private readonly ECDiffieHellman _ephemeral;
    private readonly object _cryptoGate = new();
    private ECDsa? _remoteEc;
    private RSA? _remoteRsa;
    private byte[]? _remoteRandom, _remotePoint, _master, _lastPeerFinished;
    private DtlsRecordCipher? _sendCipher, _receiveCipher;
    private SrtpContext? _srtpSender, _srtpReceiver;
    private List<FlightEntry> _flight = [];
    private Task? _pump;
    private Stage _stage;
    private ushort _sendHandshakeSequence, _expectedSequence, _cipherSuite;
    private ulong _plainRecordSequence;
    private SrtpProfile? _profile;
    private int _assemblyBytes, _started, _disposed;
    private bool _sendPending, _ccsSeen, _finishAfterSend, _negotiated, _ready;
    private long _startedAt, _readyAt, _lastFlightSentAt, _lastDuplicateResponseAt;
    private TimeSpan _retryDelay;
    private long _retransmissions, _rejected, _droppedApplication, _droppedMedia;

    public DtlsRole Role => _role;
    public int MaximumApplicationDatagramSize => _options.MaximumDatagramSize - 37;
    public bool IsConnected => Volatile.Read(ref _ready) && !_completion.Task.IsCompleted;
    public Task<Exception> Completion => _completion.Task;

    public DtlsSrtpTransport(IceUdpTransport ice, DtlsIdentity identity, DtlsRole role,
        ReadOnlySpan<byte> remoteFingerprintSha256, DtlsSrtpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ice); ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (remoteFingerprintSha256.Length != 32) throw new ArgumentException("A SHA-256 peer fingerprint is required.", nameof(remoteFingerprintSha256));
        _options = options ?? new();
        if (_options.MaximumDatagramSize is < 256 or > 1200 ||
            _options.HandshakeTimeout < TimeSpan.FromMilliseconds(100) || _options.HandshakeTimeout > TimeSpan.FromMinutes(1) ||
            _options.InitialRetransmissionTimeout < TimeSpan.FromMilliseconds(100) || _options.InitialRetransmissionTimeout > TimeSpan.FromSeconds(2) ||
            _options.Profiles is null || _options.Profiles.Count is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(options));
        _profiles = _options.Profiles.ToArray();
        if (_profiles.Distinct().Count() != _profiles.Length || _profiles.Any(p => !Enum.IsDefined(p)))
            throw new ArgumentException("Invalid SRTP profile preferences.", nameof(options));
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try { _transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); }
        catch { _ephemeral.Dispose(); throw; }
        _ice = ice; _identity = identity; _role = role;
        _remoteFingerprint = remoteFingerprintSha256.ToArray();
        _stage = role == DtlsRole.Client ? Stage.ServerHello : Stage.ClientHello;
        _application = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true }, _ => Interlocked.Increment(ref _droppedApplication));
        _media = Channel.CreateBounded<SecureMediaDatagram>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true }, _ => Interlocked.Increment(ref _droppedMedia));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_ice.IsConnected) throw new InvalidOperationException("DTLS requires a nominated ICE transport.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("DTLS has one handshake per instance.");
        _startedAt = Stopwatch.GetTimestamp();
        _pump = RunAsync();
        using var registration = cancellationToken.Register(() => _lifetime.Cancel());
        try { await _connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // WaitAsync cancellation may remove the registration before its callback runs.
            _lifetime.Cancel();
            throw;
        }
    }

    public DtlsSrtpDiagnostics GetDiagnostics() => new(_role, IsConnected ? _profile : null,
        _readyAt == 0 ? null : Stopwatch.GetElapsedTime(_startedAt, _readyAt), Interlocked.Read(ref _retransmissions),
        Interlocked.Read(ref _rejected), Interlocked.Read(ref _droppedApplication), Interlocked.Read(ref _droppedMedia));

    public IAsyncEnumerable<byte[]> ReceiveApplicationDatagramsAsync(CancellationToken cancellationToken = default) =>
        _application.Reader.ReadAllAsync(cancellationToken);
    public IAsyncEnumerable<SecureMediaDatagram> ReceiveMediaDatagramsAsync(CancellationToken cancellationToken = default) =>
        _media.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask SendApplicationDatagramAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > _options.MaximumDatagramSize - 37) throw new ArgumentOutOfRangeException(nameof(data));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            RequireReady();
            byte[] packet;
            lock (_cryptoGate) packet = _sendCipher!.Encrypt(23, data.Span);
            await _ice.SendDatagramAsync(packet, linked.Token).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    public ValueTask SendRtpAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => SendMediaAsync(data, false, cancellationToken);
    public ValueTask SendRtcpAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => SendMediaAsync(data, true, cancellationToken);
    private async ValueTask SendMediaAsync(ReadOnlyMemory<byte> data, bool rtcp, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            RequireReady();
            byte[] packet;
            lock (_cryptoGate)
            {
                var overhead = rtcp ? _srtpSender!.RtcpOverhead : _srtpSender!.RtpOverhead;
                if (data.Length > _options.MaximumDatagramSize - overhead || (!rtcp && data.Length >= 2 && (data.Span[1] & 127) is >= 64 and <= 95))
                    throw new ArgumentOutOfRangeException(nameof(data), "Invalid RTP/RTCP mux packet size or payload type.");
                packet = new byte[data.Length + overhead];
                var success = rtcp ? _srtpSender.TryProtectRtcp(data.Span, packet, out _) : _srtpSender.TryProtectRtp(data.Span, packet, out _);
                if (!success) throw new InvalidOperationException("Media packet violates framing, replay or key bounds.");
            }
            await _ice.SendDatagramAsync(packet, linked.Token).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private void RequireReady()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected) throw new InvalidOperationException("DTLS peer authentication is incomplete or closed.");
    }

    private async Task RunAsync()
    {
        Exception reason = new OperationCanceledException("DTLS transport closed.");
        try
        {
            if (_role == DtlsRole.Client)
            {
                var hello = ClientHello([]);
                QueueFlight([NewHandshake(1, hello)]);
                await SendFlightAsync(false).ConfigureAwait(false);
            }
            var receive = _ice.ReceiveDatagramsAsync(_lifetime.Token).GetAsyncEnumerator(_lifetime.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            var next = receive.MoveNextAsync().AsTask();
            var tick = timer.WaitForNextTickAsync(_lifetime.Token).AsTask();
            try
            {
                while (true)
                {
                    await Task.WhenAny(next, tick).ConfigureAwait(false);
                    if (next.IsCompleted)
                    {
                        if (!await next.ConfigureAwait(false)) throw new IOException("ICE ended before DTLS shutdown.");
                        ProcessDatagram(receive.Current);
                        if (_sendPending) await SendFlightAsync(false).ConfigureAwait(false);
                        if (_finishAfterSend) { _finishAfterSend = false; Establish(); }
                        next = receive.MoveNextAsync().AsTask();
                    }
                    if (tick.IsCompleted)
                    {
                        if (!await tick.ConfigureAwait(false)) break;
                        if (_ready && _role == DtlsRole.Server && Stopwatch.GetElapsedTime(_readyAt) >= TimeSpan.FromMinutes(4)) _flight.Clear();
                        if (!_ready && Stopwatch.GetElapsedTime(_startedAt) >= _options.HandshakeTimeout) throw new TimeoutException("DTLS handshake timed out.");
                        if (!_ready && _flight.Count != 0 && Stopwatch.GetElapsedTime(_lastFlightSentAt) >= _retryDelay)
                        {
                            await SendFlightAsync(true).ConfigureAwait(false);
                            _retryDelay = TimeSpan.FromSeconds(Math.Min(60, _retryDelay.TotalSeconds * 2));
                        }
                        tick = timer.WaitForNextTickAsync(_lifetime.Token).AsTask();
                    }
                }
            }
            finally
            {
                _lifetime.Cancel();
                try { await next.ConfigureAwait(false); } catch (Exception) { }
                await receive.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error) { reason = error; }
        finally
        {
            _lifetime.Cancel();
            Volatile.Write(ref _ready, false);
            _connected.TrySetException(reason);
            _completion.TrySetResult(reason);
            _application.Writer.TryComplete(Volatile.Read(ref _disposed) == 0 ? reason : null);
            _media.Writer.TryComplete(Volatile.Read(ref _disposed) == 0 ? reason : null);
        }
    }

    private void ProcessDatagram(byte[] datagram)
    {
        if (datagram.Length == 0 || datagram.Length > 1200) { Reject(); return; }
        if (datagram[0] is >= 128 and <= 191)
        {
            if (!_ready || datagram.Length < 2) { Reject(); return; }
            var rtcp = datagram[1] is >= 192 and <= 223;
            lock (_cryptoGate)
            {
                var size = datagram.Length - (rtcp ? _srtpReceiver!.RtcpOverhead : _srtpReceiver!.RtpOverhead);
                if (size < 0) { Reject(); return; }
                var plaintext = new byte[size];
                var success = rtcp ? _srtpReceiver.TryUnprotectRtcp(datagram, plaintext, out _) : _srtpReceiver.TryUnprotectRtp(datagram, plaintext, out _);
                if (success) _media.Writer.TryWrite(new(rtcp ? SecureMediaKind.Rtcp : SecureMediaKind.Rtp, plaintext));
                else Reject();
            }
            return;
        }
        // Validate all concatenated record boundaries before processing any record.
        var offset = 0;
        while (offset < datagram.Length)
        {
            var remaining = datagram.Length - offset;
            if (remaining < 13) { Reject(); return; }
            var size = 13 + BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(offset + 11));
            if (size > remaining) { Reject(); return; }
            offset += size;
        }
        for (offset = 0; offset < datagram.Length;)
        {
            var size = 13 + BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(offset + 11));
            ProcessRecord(datagram.AsSpan(offset, size));
            offset += size;
        }
        DrainPendingEncrypted();
    }

    private void ProcessRecord(ReadOnlySpan<byte> record)
    {
        var type = record[0];
        var version = BinaryPrimitives.ReadUInt16BigEndian(record[1..]);
        var epoch = BinaryPrimitives.ReadUInt16BigEndian(record[3..]);
        if (type is < 20 or > 23 || (version != DtlsProtocol.Version && (version != 0xFEFF || epoch != 0)) || epoch > 1)
        { Reject(); return; }
        byte[]? decrypted = null;
        var body = record[13..];
        if (epoch == 1)
        {
            if (_receiveCipher == null || !_ccsSeen)
            {
                if (!_ready && _pendingEncrypted.Count < 8) _pendingEncrypted.Add(record.ToArray());
                else Reject();
                return;
            }
            lock (_cryptoGate)
            {
                var acknowledgeReplay = type == 22 && _ready && _role == DtlsRole.Server && Stopwatch.GetElapsedTime(_readyAt) < TimeSpan.FromMinutes(4);
                if (!_receiveCipher.TryDecrypt(record, out decrypted, acknowledgeReplay)) { Reject(); return; }
            }
            body = decrypted;
        }
        else if (_ready && type != 22) { Reject(); return; }
        try
        {
            if (type == 20)
            {
                if (epoch != 0 || !body.SequenceEqual(new byte[] { 1 })) { Reject(); return; }
                _ccsSeen = true;
            }
            else if (type == 21)
            {
                if (epoch == 1 && body.Length == 2) throw new AuthenticationException("Peer closed DTLS with an authenticated alert.");
                Reject();
            }
            else if (type == 23)
            {
                if (epoch != 1) { Reject(); return; }
                if (_ready) _application.Writer.TryWrite(body.ToArray());
                else { Reject(); RequestDuplicateResponse(); }
            }
            else ProcessHandshakeFragments(body, epoch);
        }
        finally { if (decrypted != null) CryptographicOperations.ZeroMemory(decrypted); }
    }

    private void DrainPendingEncrypted()
    {
        if (_receiveCipher == null || !_ccsSeen || _pendingEncrypted.Count == 0) return;
        var records = _pendingEncrypted.ToArray(); _pendingEncrypted.Clear();
        foreach (var record in records) ProcessRecord(record);
    }

    private void ProcessHandshakeFragments(ReadOnlySpan<byte> body, ushort epoch)
    {
        // Reject malformed trailing fragments before mutating any reassembly state.
        var framing = body;
        while (!framing.IsEmpty)
        {
            if (framing.Length < 12) { Reject(); return; }
            var size = DtlsProtocol.Read24(framing[9..]);
            var total = DtlsProtocol.Read24(framing[1..]);
            var offset = DtlsProtocol.Read24(framing[6..]);
            if (size > framing.Length - 12 || total > 16384 || offset > total || size > total - offset ||
                (total != 0 && size == 0) || ((framing[0] == 20) != (epoch == 1))) { Reject(); return; }
            framing = framing[(12 + size)..];
        }
        var cursor = body;
        while (cursor.Length != 0)
        {
            if (cursor.Length < 12) { Reject(); return; }
            var size = DtlsProtocol.Read24(cursor[9..]);
            if (size > cursor.Length - 12) { Reject(); return; }
            var type = cursor[0]; var total = DtlsProtocol.Read24(cursor[1..]);
            var sequence = BinaryPrimitives.ReadUInt16BigEndian(cursor[4..]);
            var offset = DtlsProtocol.Read24(cursor[6..]);
            if (total > 16384 || offset > total || size > total - offset || (total != 0 && size == 0) ||
                ((type == 20) != (epoch == 1))) { Reject(); return; }
            if (sequence < _expectedSequence)
            {
                if (_ready)
                {
                    if (_role == DtlsRole.Server && type == 20 && epoch == 1 && offset == 0 && size == total &&
                        _lastPeerFinished != null && CryptographicOperations.FixedTimeEquals(cursor.Slice(12, size), _lastPeerFinished))
                        RequestDuplicateResponse();
                }
                else RequestDuplicateResponse();
            }
            else if (!_ready && sequence - _expectedSequence < 8)
            {
                if (!_assemblies.TryGetValue(sequence, out var assembly))
                {
                    if (_assemblyBytes + total > 32768) { Reject(); return; }
                    assembly = new(type, total, epoch); _assemblies.Add(sequence, assembly); _assemblyBytes += total;
                }
                if (!assembly.Add(type, epoch, offset, cursor.Slice(12, size))) { Reject(); return; }
            }
            else Reject();
            cursor = cursor[(12 + size)..];
        }
        while (_assemblies.TryGetValue(_expectedSequence, out var complete) && complete.Complete)
        {
            _assemblies.Remove(_expectedSequence); _assemblyBytes -= complete.Body.Length;
            try { ProcessHandshake(complete.Type, _expectedSequence, complete.Body); }
            catch (InvalidDataException) { Reject(); return; }
            if (_expectedSequence == ushort.MaxValue) throw new AuthenticationException("DTLS handshake sequence limit reached.");
            _expectedSequence++;
        }
    }

    private void ProcessHandshake(byte type, ushort sequence, byte[] body)
    {
        if (_role == DtlsRole.Client && _stage == Stage.ServerHello && type == 3)
        {
            var reader = new TlsReader(body);
            var version = reader.U16(); var cookie = reader.Vector8().ToArray(); reader.End();
            if (version is not (0xFEFF or 0xFEFD) || cookie.Length == 0 || _sendHandshakeSequence != 1 || sequence != 0)
                throw new InvalidDataException("Invalid DTLS cookie exchange.");
            _transcript.GetHashAndReset();
            QueueFlight([NewHandshake(1, ClientHello(cookie))]);
            return;
        }
        switch (_stage)
        {
            case Stage.ClientHello when type == 1:
                ProcessClientHello(sequence, body); return;
            case Stage.ServerHello when type == 2:
                ProcessServerHello(body); AddTranscript(type, sequence, body); _stage = Stage.ServerCertificate; return;
            case Stage.ServerCertificate when type == 11:
                ReadCertificate(body); AddTranscript(type, sequence, body); _stage = Stage.ServerKey; return;
            case Stage.ServerKey when type == 12:
                ReadServerKey(body); AddTranscript(type, sequence, body); _stage = Stage.CertificateRequest; return;
            case Stage.CertificateRequest when type == 13:
                ReadCertificateRequest(body); AddTranscript(type, sequence, body); _stage = Stage.ServerDone; return;
            case Stage.ServerDone when type == 14 && body.Length == 0:
                AddTranscript(type, sequence, body); ClientKeyFlight(); _stage = Stage.Finished; return;
            case Stage.ClientCertificate when type == 11:
                ReadCertificate(body); AddTranscript(type, sequence, body); _stage = Stage.ClientKey; return;
            case Stage.ClientKey when type == 16:
                var reader = new TlsReader(body); var point = reader.Vector8().ToArray(); reader.End();
                ValidatePoint(point); _remotePoint = point; AddTranscript(type, sequence, body); DeriveKeys(); _stage = Stage.CertificateVerify; return;
            case Stage.CertificateVerify when type == 15:
                VerifySignature(body, _transcript.GetCurrentHash()); AddTranscript(type, sequence, body); _stage = Stage.Finished; return;
            case Stage.Finished when type == 20:
                if (!_ccsSeen || _master == null) throw new InvalidDataException("Unexpected DTLS Finished.");
                var expected = DtlsProtocol.Prf(_master, _role == DtlsRole.Client ? "server finished"u8 : "client finished"u8, _transcript.GetCurrentHash(), 12);
                if (!CryptographicOperations.FixedTimeEquals(expected, body)) throw new AuthenticationException("DTLS Finished authentication failed.");
                _lastPeerFinished = body.ToArray(); AddTranscript(type, sequence, body);
                if (_role == DtlsRole.Server)
                {
                    var finished = DtlsProtocol.Prf(_master, "server finished"u8, _transcript.GetCurrentHash(), 12);
                    QueueFlight([new(20, 0, 0, 0, [1]), NewHandshake(20, finished, 1)]); _finishAfterSend = true;
                }
                else Establish();
                _stage = Stage.Complete; return;
            default: throw new InvalidDataException("Unexpected DTLS handshake message.");
        }
    }

    private byte[] ClientHello(ReadOnlySpan<byte> cookie)
    {
        var writer = new TlsWriter(); writer.U16(DtlsProtocol.Version); writer.Bytes(_localRandom); writer.Vector8([]); writer.Vector8(cookie);
        writer.Vector16([0xC0, 0x2B, 0xC0, 0x2F, 0x00, 0xFF]); writer.Vector8([0]);
        var extensions = new TlsWriter();
        extensions.Extension(10, [0, 2, 0, 23]); extensions.Extension(11, [1, 0]); extensions.Extension(13, [0, 4, 4, 3, 4, 1]);
        extensions.Extension(23, []);
        var srtp = new TlsWriter(); srtp.U16(_profiles.Length * 2); foreach (var profile in _profiles) srtp.U16((ushort)profile); srtp.U8(0);
        extensions.Extension(14, srtp.ToArray()); writer.Vector16(extensions.ToArray());
        return writer.ToArray();
    }

    private void ProcessClientHello(ushort sequence, byte[] body)
    {
        var reader = new TlsReader(body);
        if (reader.U16() != DtlsProtocol.Version) throw new AuthenticationException("DTLS 1.2 is required.");
        var random = reader.Read(32).ToArray(); var session = reader.Vector8().ToArray(); var cookie = reader.Vector8().ToArray();
        var ciphers = reader.Vector16().ToArray(); var compression = reader.Vector8().ToArray(); var extensions = ReadExtensions(reader.Vector16()); reader.End();
        if (session.Length > 32 || ciphers.Length == 0 || (ciphers.Length & 1) != 0 || !compression.Contains((byte)0)) throw new InvalidDataException("Invalid ClientHello framing.");
        // Cookie binds every negotiation field except the cookie itself.
        var canonical = new TlsWriter(); canonical.U16(DtlsProtocol.Version); canonical.Bytes(random); canonical.Vector8(session); canonical.Vector8([]);
        canonical.Vector16(ciphers); canonical.Vector8(compression);
        var originalExtensions = new TlsReader(body); originalExtensions.Read(34); originalExtensions.Vector8(); originalExtensions.Vector8(); originalExtensions.Vector16(); originalExtensions.Vector8();
        canonical.Vector16(originalExtensions.Vector16());
        var expectedCookie = HMACSHA256.HashData(_cookieKey, canonical.ToArray());
        if (_options.RequireCookie && !CryptographicOperations.FixedTimeEquals(cookie, expectedCookie))
        {
            if (sequence != 0) throw new InvalidDataException("Invalid cookie.");
            var verify = new TlsWriter(); verify.U16(0xFEFF); verify.Vector8(expectedCookie);
            QueueFlight([new(22, 0, 3, _sendHandshakeSequence++, verify.ToArray())]); return;
        }
        RequireExtension(extensions, 23, []);
        if (!Has16(ciphers, EcdsaCipher) || !extensions.TryGetValue(10, out var groups) || groups.Length < 2 ||
            BinaryPrimitives.ReadUInt16BigEndian(groups) != groups.Length - 2 || !Has16(groups.AsSpan(2), 23) ||
            !extensions.TryGetValue(13, out var algorithms) || algorithms.Length < 2 ||
            BinaryPrimitives.ReadUInt16BigEndian(algorithms) != algorithms.Length - 2 || !Has16(algorithms.AsSpan(2), 0x0403))
            throw new AuthenticationException("No supported DTLS cipher, P-256 group or signature algorithm.");
        var offeredProfiles = SrtpProfiles(extensions);
        var compatible = _profiles.Where(p => offeredProfiles.Contains(p)).ToArray();
        if (compatible.Length == 0) throw new AuthenticationException("No compatible SRTP profile.");
        _remoteRandom = random; _cipherSuite = EcdsaCipher; _profile = compatible[0]; _negotiated = true;
        AddTranscript(1, sequence, body);
        var hello = new TlsWriter(); hello.U16(DtlsProtocol.Version); hello.Bytes(_localRandom); hello.Vector8([]); hello.U16(_cipherSuite); hello.U8(0);
        var helloExtensions = new TlsWriter(); helloExtensions.Extension(23, []); helloExtensions.Extension(11, [1, 0]);
        var srtp = new TlsWriter(); srtp.U16(2); srtp.U16((ushort)_profile.Value); srtp.U8(0); helloExtensions.Extension(14, srtp.ToArray());
        if (Has16(ciphers, 0x00FF) || extensions.ContainsKey(0xFF01)) helloExtensions.Extension(0xFF01, [0]);
        hello.Vector16(helloExtensions.ToArray());
        var publicPoint = LocalPoint(); var key = new TlsWriter(); key.U8(3); key.U16(23); key.Vector8(publicPoint);
        var parameters = key.ToArray(); var signed = new byte[64 + parameters.Length]; random.CopyTo(signed, 0); _localRandom.CopyTo(signed, 32); parameters.CopyTo(signed, 64);
        key.Bytes(Signature(_identity.Sign(SHA256.HashData(signed))));
        var request = new TlsWriter(); request.Vector8([64, 1]); request.Vector16([4, 3, 4, 1]); request.U16(0);
        QueueFlight([NewHandshake(2, hello.ToArray()), NewHandshake(11, CertificateBody()), NewHandshake(12, key.ToArray()),
            NewHandshake(13, request.ToArray()), NewHandshake(14, [])]);
        _stage = Stage.ClientCertificate;
    }

    private void ProcessServerHello(byte[] body)
    {
        var reader = new TlsReader(body);
        if (reader.U16() != DtlsProtocol.Version) throw new AuthenticationException("DTLS 1.2 is required.");
        var random = reader.Read(32).ToArray(); var session = reader.Vector8();
        var cipher = reader.U16(); var compression = reader.U8(); var extensions = ReadExtensions(reader.Vector16()); reader.End();
        if (session.Length > 32 || compression != 0 || cipher is not (EcdsaCipher or RsaCipher)) throw new AuthenticationException("Unsupported DTLS negotiation.");
        RequireExtension(extensions, 23, []);
        var profiles = SrtpProfiles(extensions);
        if (profiles.Length != 1 || !_profiles.Contains(profiles[0])) throw new AuthenticationException("Invalid SRTP profile selection.");
        foreach (var type in extensions.Keys)
            if (type is not (10 or 11 or 13 or 14 or 23 or 0xFF01)) throw new AuthenticationException("Unsolicited DTLS extension.");
        if (extensions.TryGetValue(0xFF01, out var renegotiation) && !renegotiation.AsSpan().SequenceEqual(new byte[] { 0 }))
            throw new AuthenticationException("DTLS renegotiation is not supported.");
        _remoteRandom = random; _cipherSuite = cipher; _profile = profiles[0]; _negotiated = true;
    }

    private static Dictionary<ushort, byte[]> ReadExtensions(ReadOnlySpan<byte> data)
    {
        var reader = new TlsReader(data); var extensions = new Dictionary<ushort, byte[]>();
        while (reader.Remaining != 0)
        {
            var type = reader.U16(); var body = reader.Vector16();
            if (extensions.Count == 32 || !extensions.TryAdd(type, body.ToArray())) throw new InvalidDataException("DTLS extension bounds/duplicate.");
        }
        return extensions;
    }

    private static void RequireExtension(Dictionary<ushort, byte[]> extensions, ushort type, ReadOnlySpan<byte> expected)
    {
        if (!extensions.TryGetValue(type, out var value) || !value.AsSpan().SequenceEqual(expected))
            throw new AuthenticationException("Required DTLS extension is missing or invalid.");
    }

    private static bool Has16(ReadOnlySpan<byte> values, ushort expected)
    {
        if ((values.Length & 1) != 0) return false;
        for (var offset = 0; offset < values.Length; offset += 2)
            if (BinaryPrimitives.ReadUInt16BigEndian(values[offset..]) == expected) return true;
        return false;
    }

    private static SrtpProfile[] SrtpProfiles(Dictionary<ushort, byte[]> extensions)
    {
        if (!extensions.TryGetValue(14, out var data)) throw new AuthenticationException("DTLS-SRTP extension required.");
        var reader = new TlsReader(data); var values = reader.Vector16(); var mki = reader.Vector8(); reader.End();
        if (values.Length == 0 || values.Length > 32 || (values.Length & 1) != 0 || mki.Length != 0) throw new AuthenticationException("Invalid SRTP profile list or unsupported MKI.");
        var profiles = new SrtpProfile[values.Length / 2];
        for (var i = 0; i < profiles.Length; i++) profiles[i] = (SrtpProfile)BinaryPrimitives.ReadUInt16BigEndian(values[(2 * i)..]);
        if (profiles.Distinct().Count() != profiles.Length) throw new AuthenticationException("Duplicate SRTP profiles.");
        return profiles;
    }

    private byte[] CertificateBody()
    {
        var certificate = _identity.Certificate; var writer = new TlsWriter(); writer.U24(certificate.Length + 3); writer.U24(certificate.Length); writer.Bytes(certificate); return writer.ToArray();
    }

    private void ReadCertificate(byte[] body)
    {
        var reader = new TlsReader(body); var list = new TlsReader(reader.Read(reader.U24())); reader.End();
        var certificate = list.Read(list.U24()).ToArray();
        for (var count = 1; list.Remaining != 0; count++)
        {
            if (count >= 8) throw new AuthenticationException("DTLS certificate chain too long.");
            var size = list.U24(); if (size == 0) throw new InvalidDataException("Empty DTLS certificate."); list.Read(size);
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate), _remoteFingerprint))
            throw new AuthenticationException("DTLS certificate fingerprint mismatch.");
        try
        {
            using var leaf = X509CertificateLoader.LoadCertificate(certificate);
            var ec = leaf.GetECDsaPublicKey(); var rsa = leaf.GetRSAPublicKey();
            if (ec != null && ec.KeySize is >= 256 and <= 521) { _remoteEc = ec; rsa?.Dispose(); }
            else if (rsa != null && rsa.KeySize is >= 2048 and <= 4096) { _remoteRsa = rsa; ec?.Dispose(); }
            else { ec?.Dispose(); rsa?.Dispose(); throw new AuthenticationException("Unsupported peer certificate key."); }
        }
        catch (CryptographicException error) { throw new AuthenticationException("Invalid DTLS certificate.", error); }
    }

    private void ReadServerKey(byte[] body)
    {
        var reader = new TlsReader(body);
        if (reader.U8() != 3 || reader.U16() != 23) throw new AuthenticationException("DTLS ECDHE requires P-256.");
        var point = reader.Vector8().ToArray(); ValidatePoint(point);
        var parametersLength = body.Length - reader.Remaining;
        var signed = new byte[64 + parametersLength]; _localRandom.CopyTo(signed, 0); _remoteRandom!.CopyTo(signed, 32); body.AsSpan(0, parametersLength).CopyTo(signed.AsSpan(64));
        if ((_cipherSuite == EcdsaCipher && _remoteEc == null) || (_cipherSuite == RsaCipher && _remoteRsa == null))
            throw new AuthenticationException("DTLS cipher and certificate do not agree.");
        VerifySignature(reader.Read(reader.Remaining), SHA256.HashData(signed)); _remotePoint = point;
    }

    private static void ValidatePoint(ReadOnlySpan<byte> point)
    {
        if (point.Length != 65 || point[0] != 4) throw new AuthenticationException("Invalid P-256 ECDHE point.");
        try
        {
            using var key = ECDiffieHellman.Create(new ECParameters
            { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() } });
        }
        catch (CryptographicException error) { throw new AuthenticationException("Invalid P-256 ECDHE point.", error); }
    }

    private byte[] LocalPoint()
    {
        var parameters = _ephemeral.ExportParameters(false); var point = new byte[65]; point[0] = 4;
        parameters.Q.X!.CopyTo(point, 1); parameters.Q.Y!.CopyTo(point, 33); return point;
    }

    private static byte[] Signature(byte[] signature)
    { var writer = new TlsWriter(); writer.U16(0x0403); writer.Vector16(signature); return writer.ToArray(); }

    private void VerifySignature(ReadOnlySpan<byte> body, ReadOnlySpan<byte> hash)
    {
        var reader = new TlsReader(body); var algorithm = reader.U16(); var signature = reader.Vector16(); reader.End();
        var valid = algorithm switch
        {
            0x0403 when _remoteEc != null => _remoteEc.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence),
            0x0401 when _remoteRsa != null => _remoteRsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => false,
        };
        if (!valid) throw new AuthenticationException("DTLS handshake signature mismatch.");
    }

    private static void ReadCertificateRequest(byte[] body)
    {
        var reader = new TlsReader(body); var types = reader.Vector8(); var algorithms = reader.Vector16();
        var authorities = new TlsReader(reader.Vector16()); reader.End();
        while (authorities.Remaining != 0) authorities.Vector16();
        if (!types.Contains((byte)64) || !Has16(algorithms, 0x0403)) throw new AuthenticationException("Peer cannot authenticate our P-256 identity.");
    }

    private void ClientKeyFlight()
    {
        var flight = new List<FlightEntry> { NewHandshake(11, CertificateBody()) };
        var exchange = new TlsWriter(); exchange.Vector8(LocalPoint()); flight.Add(NewHandshake(16, exchange.ToArray()));
        DeriveKeys();
        flight.Add(NewHandshake(15, Signature(_identity.Sign(_transcript.GetCurrentHash()))));
        flight.Add(new(20, 0, 0, 0, [1]));
        flight.Add(NewHandshake(20, DtlsProtocol.Prf(_master!, "client finished"u8, _transcript.GetCurrentHash(), 12), 1));
        QueueFlight(flight);
    }

    private void DeriveKeys()
    {
        if (!_negotiated || _remotePoint == null || _remoteRandom == null || _master != null) throw new AuthenticationException("Invalid DTLS key derivation state.");
        using var remote = ECDiffieHellman.Create(new ECParameters
        { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = _remotePoint.AsSpan(1, 32).ToArray(), Y = _remotePoint.AsSpan(33, 32).ToArray() } });
        var secret = _ephemeral.DeriveRawSecretAgreement(remote.PublicKey);
        try { _master = DtlsProtocol.Prf(secret, "extended master secret"u8, _transcript.GetCurrentHash(), 48); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        var randoms = new byte[64]; ServerRandom.CopyTo(randoms, 0); ClientRandom.CopyTo(randoms, 32);
        var block = DtlsProtocol.Prf(_master, "key expansion"u8, randoms, 40);
        try
        {
            lock (_cryptoGate)
            {
                _sendCipher = new(block.AsSpan(_role == DtlsRole.Client ? 0 : 16, 16), block.AsSpan(_role == DtlsRole.Client ? 32 : 36, 4));
                _receiveCipher = new(block.AsSpan(_role == DtlsRole.Client ? 16 : 0, 16), block.AsSpan(_role == DtlsRole.Client ? 36 : 32, 4));
            }
        }
        finally { CryptographicOperations.ZeroMemory(block); }
    }

    private byte[] ClientRandom => _role == DtlsRole.Client ? _localRandom : _remoteRandom!;
    private byte[] ServerRandom => _role == DtlsRole.Server ? _localRandom : _remoteRandom!;

    private void Establish()
    {
        if (_master == null || _profile == null) throw new AuthenticationException("Incomplete DTLS key negotiation.");
        var keyLength = _profile == SrtpProfile.AeadAes256Gcm ? 32 : 16;
        var saltLength = _profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12;
        var randoms = new byte[64]; ClientRandom.CopyTo(randoms, 0); ServerRandom.CopyTo(randoms, 32);
        var material = DtlsProtocol.Prf(_master, "EXTRACTOR-dtls_srtp"u8, randoms, 2 * (keyLength + saltLength));
        try
        {
            lock (_cryptoGate)
            {
                var localOffset = _role == DtlsRole.Client ? 0 : 1; var remoteOffset = 1 - localOffset;
                _srtpSender = new(_profile.Value, SrtpDirection.Send, material.AsSpan(localOffset * keyLength, keyLength), material.AsSpan(2 * keyLength + localOffset * saltLength, saltLength));
                _srtpReceiver = new(_profile.Value, SrtpDirection.Receive, material.AsSpan(remoteOffset * keyLength, keyLength), material.AsSpan(2 * keyLength + remoteOffset * saltLength, saltLength));
            }
        }
        finally { CryptographicOperations.ZeroMemory(material); }
        CryptographicOperations.ZeroMemory(_master); _master = null;
        _ephemeral.Dispose();
        _transcript.Dispose();
        _readyAt = Stopwatch.GetTimestamp(); Volatile.Write(ref _ready, true); _connected.TrySetResult();
        _assemblies.Clear(); _assemblyBytes = 0; _pendingEncrypted.Clear();
        if (_role == DtlsRole.Client) _flight.Clear();
    }

    private FlightEntry NewHandshake(byte type, byte[] body, ushort epoch = 0)
    {
        if (_sendHandshakeSequence == ushort.MaxValue) throw new AuthenticationException("DTLS handshake sequence exhausted.");
        var entry = new FlightEntry(22, epoch, type, _sendHandshakeSequence++, body);
        AddTranscript(type, entry.Sequence, body); return entry;
    }
    private void AddTranscript(byte type, ushort sequence, byte[] body) => _transcript.AppendData(DtlsProtocol.Handshake(type, sequence, body));
    private void QueueFlight(List<FlightEntry> flight)
    { _flight = flight; _sendPending = true; _retryDelay = _options.InitialRetransmissionTimeout; }
    private void RequestDuplicateResponse()
    {
        if (_flight.Count == 0 || (_ready && (_role != DtlsRole.Server || Stopwatch.GetElapsedTime(_readyAt) >= TimeSpan.FromMinutes(4))) ||
            (_lastDuplicateResponseAt != 0 && Stopwatch.GetElapsedTime(_lastDuplicateResponseAt) < TimeSpan.FromMilliseconds(100))) return;
        _lastDuplicateResponseAt = Stopwatch.GetTimestamp(); _sendPending = true; Interlocked.Increment(ref _retransmissions);
    }

    private async Task SendFlightAsync(bool retry)
    {
        await _sendGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            foreach (var entry in _flight)
            {
                var fragments = new List<byte[]>();
                if (entry.Type == 22)
                {
                    var maximum = _options.MaximumDatagramSize - 25 - (entry.Epoch == 1 ? 24 : 0);
                    var offset = 0;
                    do
                    {
                        var size = Math.Min(maximum, entry.Body.Length - offset);
                        fragments.Add(DtlsProtocol.Handshake(entry.HandshakeType, entry.Sequence, entry.Body, offset, size)); offset += size;
                    } while (offset < entry.Body.Length);
                }
                else fragments.Add(entry.Body);
                foreach (var fragment in fragments)
                {
                    byte[] packet;
                    lock (_cryptoGate)
                        packet = entry.Epoch == 0 ? DtlsProtocol.Record(entry.Type, 0, _plainRecordSequence++, fragment) : _sendCipher!.Encrypt(entry.Type, fragment);
                    await _ice.SendDatagramAsync(packet, _lifetime.Token).ConfigureAwait(false);
                }
            }
            _sendPending = false; _lastFlightSentAt = Stopwatch.GetTimestamp();
            if (retry) Interlocked.Increment(ref _retransmissions);
        }
        finally { _sendGate.Release(); }
    }

    private void Reject() => Interlocked.Increment(ref _rejected);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_pump != null) await _pump.ConfigureAwait(false);
        else
        {
            var reason = new ObjectDisposedException(nameof(DtlsSrtpTransport));
            _connected.TrySetException(reason); _completion.TrySetResult(reason);
            _application.Writer.TryComplete(); _media.Writer.TryComplete();
        }
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_cryptoGate)
            {
                _sendCipher?.Dispose(); _receiveCipher?.Dispose(); _srtpSender?.Dispose(); _srtpReceiver?.Dispose();
                _remoteEc?.Dispose(); _remoteRsa?.Dispose(); _ephemeral.Dispose(); _transcript.Dispose();
                if (_master != null) CryptographicOperations.ZeroMemory(_master);
                CryptographicOperations.ZeroMemory(_cookieKey);
                _assemblies.Clear(); _pendingEncrypted.Clear(); _flight.Clear();
            }
        }
        finally { _sendGate.Release(); }
        _lifetime.Dispose();
    }

    private enum Stage { ClientHello, ServerHello, ServerCertificate, ServerKey, CertificateRequest, ServerDone, ClientCertificate, ClientKey, CertificateVerify, Finished, Complete }
    private sealed record FlightEntry(byte Type, ushort Epoch, byte HandshakeType, ushort Sequence, byte[] Body);
    private sealed class Assembly(byte type, int size, ushort epoch)
    {
        internal readonly byte Type = type;
        internal readonly ushort Epoch = epoch;
        internal readonly byte[] Body = new byte[size];
        private readonly bool[] _present = new bool[size];
        private int _count;
        internal bool Complete => _count == Body.Length;
        internal bool Add(byte fragmentType, ushort fragmentEpoch, int offset, ReadOnlySpan<byte> bytes)
        {
            if (fragmentType != Type || fragmentEpoch != Epoch || offset > Body.Length || bytes.Length > Body.Length - offset) return false;
            for (var i = 0; i < bytes.Length; i++) if (_present[offset + i] && Body[offset + i] != bytes[i]) return false;
            for (var i = 0; i < bytes.Length; i++) if (!_present[offset + i]) { _present[offset + i] = true; Body[offset + i] = bytes[i]; _count++; }
            return true;
        }
    }
}
