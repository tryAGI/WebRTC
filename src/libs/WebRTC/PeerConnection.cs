using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Text;

namespace tryAGI.WebRTC;

public enum PeerConnectionState { New, HaveLocalOffer, Ready, Connecting, Connected, Failed, Closed }
public sealed record PeerConnectionOptions
{
    /// <summary>A resolved local interface address; unspecified/multicast binds cannot be advertised.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }
    public bool DataChannels { get; init; } = true;
    public SdpDirection AudioDirection { get; init; } = SdpDirection.SendReceive;
    public IceUdpTransportOptions Ice { get; init; } = new();
    public DtlsSrtpOptions Dtls { get; init; } = new();
    public SctpOptions Sctp { get; init; } = new();
    public DataChannelLimits Channels { get; init; } = new();
    public int AudioQueueCapacity { get; init; } = 8;
    public int ControlQueueCapacity { get; init; } = 32;
    public int MaximumAudioSources { get; init; } = 8;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional application destination policy, evaluated before ICE admits a candidate.</summary>
    public Func<IceCandidate, bool>? CandidateFilter { get; init; }
}
public sealed record PeerConnectionDiagnostics(PeerConnectionState State, TimeSpan? MediaReadyTime, TimeSpan? ConnectedTime,
    long RejectedAudioPackets, long RejectedControlPackets, long DroppedAudioPackets, long DroppedControlPackets,
    IceUdpTransportDiagnostics Ice, DtlsSrtpDiagnostics? Dtls);

/// <summary>Owns initial Opus/data BUNDLE with a resolved host base, bounded explicit STUN/UDP TURN gathering. No TCP/TLS relays, video, codec engine or jitter buffer.</summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PeerConnectionOptions _options;
    private readonly IceUdpTransport _ice;
    private readonly DtlsIdentity _identity;
    private readonly IPEndPoint _endpoint;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _audioSend = new(1, 1);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mediaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dataReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<EncodedOpusPacket> _audio;
    private readonly Channel<byte[]> _control;
    private PeerConnectionState _state;
    private SdpSessionDescription? _local, _remote;
    private SdpNegotiatedSession? _session;
    private PeerAudio? _routing;
    private DtlsSrtpTransport? _dtls;
    private SctpAssociation? _sctp;
    private DataChannelAssociation? _channels;
    private Task? _run, _dispose;
    private bool _disposed;
    private readonly List<IceCandidate> _localCandidates = [];
    private int _activeGathering;
    private bool _gatheringComplete;
    private ushort _sequence = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
    private long _startedAt, _mediaAt, _connectedAt, _rejectedAudio, _rejectedControl, _droppedAudio, _droppedControl;
    public uint AudioSource { get; }
    public PeerConnectionState State { get { lock (_gate) return _state; } }
    public Task MediaReady => _mediaReady.Task;
    public Task DataChannelsReady => _dataReady.Task;
    public Task<Exception?> Completion => _completion.Task;
    public string? LocalDescription { get; private set; }
    public int MaximumAudioPayloadBytes { get { lock (_gate) return _routing == null ? 0 : _options.Dtls.MaximumDatagramSize - _routing.HeaderLength - 16; } }

    public PeerConnection(PeerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.LocalEndPoint);
        if (options.Ice == null || options.Dtls == null || options.Sctp == null || options.Channels == null ||
            !Enum.IsDefined(options.AudioDirection) || options.AudioQueueCapacity is < 1 or > 1024 ||
            options.ControlQueueCapacity is < 1 or > 128 || options.MaximumAudioSources is < 1 or > 64 ||
            options.ConnectionTimeout < TimeSpan.FromMilliseconds(100) || options.ConnectionTimeout > TimeSpan.FromMinutes(2) ||
            options.Sctp.MaximumMessageSize is < 1 or > 1048576 || options.Sctp.LocalPort == 0 ||
            options.Channels.ReceiveBufferBytes is < 1 or > 8 * 1024 * 1024 ||
            options.Sctp.ReceiveBufferBytes < Math.Max(1500, options.Sctp.MaximumMessageSize) ||
            options.Sctp.SendBufferBytes < options.Sctp.MaximumMessageSize) throw new ArgumentOutOfRangeException(nameof(options));
        _ = new IceCandidate(new(options.LocalEndPoint.Address, options.LocalEndPoint.Port == 0 ? 1 : options.LocalEndPoint.Port));
        _options = options; _identity = DtlsIdentity.Generate();
        try { _ice = new(new(options.LocalEndPoint.Address, options.LocalEndPoint.Port), options: options.Ice with
            { RemoteCandidateFilter = SupportedCandidate }); }
        catch { _identity.Dispose(); throw; }
        _endpoint = _ice.LocalEndPoint;
        do { AudioSource = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)); } while (AudioSource == 0);
        _audio = Channel.CreateBounded<EncodedOpusPacket>(new BoundedChannelOptions(options.AudioQueueCapacity)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true }, _ => Interlocked.Increment(ref _droppedAudio));
        _control = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.ControlQueueCapacity)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true }, _ => Interlocked.Increment(ref _droppedControl));
    }
    private SdpLocalTransport LocalTransport() => new(_ice.LocalCredentials, _identity.GetFingerprintSha256(), _endpoint,
        _options.Sctp.LocalPort, Math.Min(_options.Sctp.MaximumMessageSize, _options.Channels.ReceiveBufferBytes), _localCandidates, _gatheringComplete, true, _options.Ice.RelayOnly);
    public IReadOnlyList<IceCandidate> GetLocalCandidates()
    { lock (_gate) { RequireOpen(); return LocalTransport().Candidates; } }
    public StunGatheringDiagnostics GetGatheringDiagnostics() => _ice.GetGatheringDiagnostics();
    /// <summary>Explicit resolved STUN server only. Caller cancellation stops this gather, preserving the peer and its socket.</summary>
    public Task<IceCandidate> GatherServerReflexiveCandidateAsync(IPEndPoint server, StunGatheringOptions? options = null,
        CancellationToken cancellationToken = default) => GatherCandidateAsync(
            ct => _ice.GatherServerReflexiveCandidateAsync(server, options, ct), cancellationToken);

    /// <summary>Owns one explicit UDP TURN allocation and updates this generation's SDP. Caller cancellation preserves previously attached paths.</summary>
    public Task<IceCandidate> GatherRelayCandidateAsync(IPEndPoint server, TurnCredentials credentials, TurnUdpOptions? options = null,
        CancellationToken cancellationToken = default) => GatherCandidateAsync(
            ct => _ice.GatherRelayCandidateAsync(server, credentials, options, ct), cancellationToken);

    private async Task<IceCandidate> GatherCandidateAsync(Func<CancellationToken, Task<IceCandidate>> gather, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RequireOpen();
            if (_gatheringComplete || _localCandidates.Count + _activeGathering >= 8)
                throw new InvalidOperationException("Gathering is complete or its bounded candidate budget is reserved.");
            _activeGathering++;
        }
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var candidate = await gather(lifetime.Token).ConfigureAwait(false);
            lock (_gate)
            {
                RequireOpen();
                if (!candidate.EndPoint.Equals(_endpoint) && !_localCandidates.Any(c => c.EndPoint.Equals(candidate.EndPoint)))
                    _localCandidates.Add(candidate);
                RefreshLocalDescription();
            }
            return candidate;
        }
        finally { lock (_gate) _activeGathering--; }
    }
    /// <summary>Marks this initial generation's local gathering complete. No active gathering request may remain.</summary>
    public void CompleteGathering()
    {
        lock (_gate)
        {
            RequireOpen();
            if (_activeGathering != 0) throw new InvalidOperationException("Gathering requests are still active.");
            _gatheringComplete = true; RefreshLocalDescription();
        }
    }
    private void RefreshLocalDescription()
    {
        if (_local == null) return;
        var transport = LocalTransport(); var builder = new StringBuilder(); var mediaIndex = 0; var active = false;
        // Only candidate metadata changes; preserve the initial session id,
        // negotiated roles, codecs, source and authenticated signaling material.
        foreach (var line in LocalDescription!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (active) SdpNegotiation.WriteCandidates(builder, transport);
                active = !_local.Media[mediaIndex++].IsRejected;
            }
            if (line.StartsWith("a=candidate:", StringComparison.Ordinal) || line == "a=end-of-candidates") continue;
            builder.Append(line).Append("\r\n");
        }
        if (active) SdpNegotiation.WriteCandidates(builder, transport);
        var text = builder.ToString(); _local = SdpSessionDescription.Parse(text); LocalDescription = text;
    }
    public string CreateOffer()
    {
        lock (_gate)
        {
            RequireState(PeerConnectionState.New);
            var text = SdpNegotiation.CreateOpusOffer(LocalTransport(), AudioSource, _options.DataChannels, _options.AudioDirection);
            _local = SdpSessionDescription.Parse(text); LocalDescription = text; _state = PeerConnectionState.HaveLocalOffer; return text;
        }
    }
    public string CreateAnswer(string remoteOffer, SdpSetup preferredSetup = SdpSetup.Active)
    {
        var remote = SdpSessionDescription.Parse(remoteOffer);
        lock (_gate)
        {
            RequireState(PeerConnectionState.New);
            var text = SdpNegotiation.CreateOpusAnswer(remote, LocalTransport(), AudioSource, _options.DataChannels, preferredSetup, _options.AudioDirection);
            var local = SdpSessionDescription.Parse(text);
            var session = SdpNegotiation.ValidateOpusAnswer(remote, local, false);
            var routing = new PeerAudio(session, remote, AudioSource, _options.MaximumAudioSources);
            _remote = remote; _local = local; _session = session; _routing = routing;
            LocalDescription = text; _state = PeerConnectionState.Ready; return text;
        }
    }
    public void SetRemoteAnswer(string remoteAnswer)
    {
        var remote = SdpSessionDescription.Parse(remoteAnswer);
        lock (_gate)
        {
            RequireState(PeerConnectionState.HaveLocalOffer);
            var session = SdpNegotiation.ValidateOpusAnswer(_local!, remote, true);
            var routing = new PeerAudio(session, remote, AudioSource, _options.MaximumAudioSources);
            _remote = remote; _session = session; _routing = routing; _state = PeerConnectionState.Ready;
        }
    }
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RequireState(PeerConnectionState.Ready);
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = _session!.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()).OfType<IceCandidate>()
                .Where(SupportedCandidate).DistinctBy(c => c.EndPoint.ToString()).Take(_options.Ice.MaximumCandidatePairs + 1).ToArray();
            if (candidates.Length > _options.Ice.MaximumCandidatePairs) throw new ArgumentException("Remote candidate limit exceeded.");
            RequireState(PeerConnectionState.Ready);
            _state = PeerConnectionState.Connecting; _startedAt = Stopwatch.GetTimestamp();
            _run = RunAsync(candidates, cancellationToken); return _connected.Task;
        }
    }
    private bool SupportedCandidate(IceCandidate candidate) => candidate.EndPoint.AddressFamily == _endpoint.AddressFamily &&
        (_options.CandidateFilter?.Invoke(candidate) ?? true) && (_options.Ice.RemoteCandidateFilter?.Invoke(candidate) ?? true);
    /// <summary>Candidate attribute body, without the a=candidate: prefix; only the active credential generation is accepted.</summary>
    public void AddRemoteCandidate(string candidate)
    {
        var parsed = SdpIceCandidate.Parse(candidate).GetResolvedUdpCandidate();
        if (parsed == null || !SupportedCandidate(parsed)) throw new NotSupportedException("Candidate requires unsupported resolution, address family or destination.");
        lock (_gate)
        {
            RequireOpen();
            if (_state is not (PeerConnectionState.Connecting or PeerConnectionState.Connected)) throw new InvalidOperationException("ICE generation has not started.");
            _ice.AddRemoteCandidate(parsed);
        }
    }
    public async ValueTask SendOpusAsync(ReadOnlyMemory<byte> payload, uint rtpTimestamp, bool marker = false, CancellationToken cancellationToken = default)
    {
        lock (_gate) RequireOpen();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _audioSend.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) RequireOpen();
            if (!_mediaReady.Task.IsCompletedSuccessfully || !_session!.CanSendAudio) throw new InvalidOperationException("Sending Opus was not negotiated or media is not ready.");
            if (payload.Length < 1 || payload.Length > MaximumAudioPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload));
            var packet = _routing!.Write(payload.Span, _sequence++, rtpTimestamp, marker);
            // Consume the sequence even if transmission is cancelled after protection; SRTP indexes cannot be reused.
            await _dtls!.SendRtpAsync(packet, linked.Token).ConfigureAwait(false);
        }
        finally { _audioSend.Release(); }
    }
    public IAsyncEnumerable<EncodedOpusPacket> ReceiveAudioAsync(CancellationToken cancellationToken = default) => _audio.Reader.ReadAllAsync(cancellationToken);
    public IAsyncEnumerable<byte[]> ReceiveRtcpAsync(CancellationToken cancellationToken = default) => _control.Reader.ReadAllAsync(cancellationToken);
    public ValueTask SendRtcpAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        lock (_gate) RequireOpen();
        if (!_mediaReady.Task.IsCompletedSuccessfully) throw new InvalidOperationException("Secure media is not ready.");
        if (!RtcpFraming.IsValid(packet.Span) || BinaryPrimitives.ReadUInt32BigEndian(packet.Span[4..]) != AudioSource)
            throw new ArgumentException("RTCP requires valid framing and the local sender source.", nameof(packet));
        return _dtls!.SendRtcpAsync(packet, cancellationToken);
    }
    public async Task<DataChannel> OpenDataChannelAsync(DataChannelParameters parameters, CancellationToken cancellationToken = default)
    {
        RequireData(); await _dataReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _channels!.OpenChannelAsync(parameters, cancellationToken).ConfigureAwait(false);
    }
    public async IAsyncEnumerable<DataChannel> AcceptDataChannelsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireData(); await _dataReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var channel in _channels!.AcceptChannelsAsync(cancellationToken).ConfigureAwait(false)) yield return channel;
    }
    private void RequireData()
    {
        lock (_gate)
        {
            RequireOpen();
            if (_session == null || _session.LocalData == null) throw new NotSupportedException("Data channels were not negotiated.");
        }
    }
    private async Task RunAsync(IceCandidate[] candidates, CancellationToken caller)
    {
        Exception? reason = null; Task? media = null;
        using var establishment = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        establishment.CancelAfter(_options.ConnectionTimeout);
        try
        {
            await _ice.ConnectAsync(_session!.RemoteCredentials, _session.IceRole, candidates, establishment.Token).ConfigureAwait(false);
            _dtls = new(_ice, _identity, _session.DtlsRole, Convert.FromHexString(_session.RemoteFingerprintSha256), _options.Dtls);
            await _dtls.ConnectAsync(establishment.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _mediaAt, Stopwatch.GetTimestamp()); _mediaReady.TrySetResult();
            media = ReceiveMediaAsync();
            if (_session.LocalData != null)
            {
                var sctpOptions = _options.Sctp with { LocalPort = _session.LocalData.SctpPort!.Value, RemotePort = _session.RemoteData!.SctpPort!.Value,
                    MaximumMessageSize = _session.MaximumMessageSize, MaximumPacketSize = Math.Min(_options.Sctp.MaximumPacketSize, _dtls.MaximumApplicationDatagramSize) };
                _sctp = new(_dtls, _session.DtlsRole == DtlsRole.Client ? SctpRole.Initiator : SctpRole.Responder, sctpOptions);
                await _sctp.ConnectAsync(establishment.Token).ConfigureAwait(false);
                _channels = new(_sctp, _options.Channels); _dataReady.TrySetResult();
            }
            else _dataReady.TrySetException(new NotSupportedException("Data channels were not negotiated."));
            establishment.Token.ThrowIfCancellationRequested();
            lock (_gate) { RequireOpen(); _state = PeerConnectionState.Connected; }
            Interlocked.Exchange(ref _connectedAt, Stopwatch.GetTimestamp()); _connected.TrySetResult();
            establishment.Dispose();
            // Caller cancellation applies to establishment only, including a stalled SCTP handshake.
            var ends = new List<Task> { _ice.Completion, _dtls.Completion, media, Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token) };
            if (_channels != null) ends.Add(_channels.Completion);
            await Task.WhenAny(ends).ConfigureAwait(false);
            if (_channels?.Completion.IsCompleted == true) reason = await _channels.Completion.ConfigureAwait(false);
            else if (_dtls.Completion.IsCompleted) reason = await _dtls.Completion.ConfigureAwait(false);
            else if (_ice.Completion.IsCompleted) reason = await _ice.Completion.ConfigureAwait(false);
            else if (media.IsCompleted) { await media.ConfigureAwait(false); reason = new IOException("Secure media stream ended."); }
        }
        catch (Exception error) { reason = error; }
        finally
        {
            _lifetime.Cancel();
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception error) { reason ??= error; }
            if (media != null) { try { await media.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception error) { reason ??= error; } }
            Finish(reason);
        }
    }
    private async Task ReceiveMediaAsync()
    {
        await foreach (var datagram in _dtls!.ReceiveMediaDatagramsAsync(_lifetime.Token).ConfigureAwait(false))
        {
            if (datagram.Kind == SecureMediaKind.Rtp)
            {
                var packet = _routing!.Read(datagram.Data);
                if (packet == null) Interlocked.Increment(ref _rejectedAudio); else _audio.Writer.TryWrite(packet);
            }
            else if (!RtcpFraming.IsValid(datagram.Data)) Interlocked.Increment(ref _rejectedControl);
            else _control.Writer.TryWrite(datagram.Data);
        }
    }
    private async Task CleanupAsync()
    {
        try { if (_channels != null) await _channels.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { if (_sctp != null) await _sctp.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { if (_dtls != null) await _dtls.DisposeAsync().ConfigureAwait(false); }
                finally { try { await _ice.DisposeAsync().ConfigureAwait(false); } finally { _identity.Dispose(); } }
            }
        }
    }
    private void Finish(Exception? reason)
    {
        lock (_gate) { if (_disposed) reason = null; _state = reason == null ? PeerConnectionState.Closed : PeerConnectionState.Failed; }
        var failure = reason ?? new ObjectDisposedException(nameof(PeerConnection));
        _connected.TrySetException(failure); _mediaReady.TrySetException(failure); _dataReady.TrySetException(failure);
        _audio.Writer.TryComplete(reason); _control.Writer.TryComplete(reason); _completion.TrySetResult(reason);
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose == null) { _disposed = true; _lifetime.Cancel(); _dispose = DisposeCoreAsync(); }
            return new(_dispose);
        }
    }
    /// <summary>Drains and shuts down negotiated SCTP, then releases all owned resources. Cancellation still releases the local owner.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        try { if (_sctp?.IsConnected == true) await _sctp.CloseAsync(cancellationToken).ConfigureAwait(false); }
        finally { await DisposeAsync().ConfigureAwait(false); }
    }
    private async Task DisposeCoreAsync()
    {
        if (_run != null) await _run.ConfigureAwait(false);
        else { await CleanupAsync().ConfigureAwait(false); Finish(null); }
        lock (_gate) _state = PeerConnectionState.Closed;
        _lifetime.Dispose();
    }
    public PeerConnectionDiagnostics GetDiagnostics() => new(State, Elapsed(_mediaAt), Elapsed(_connectedAt),
        Interlocked.Read(ref _rejectedAudio), Interlocked.Read(ref _rejectedControl), Interlocked.Read(ref _droppedAudio), Interlocked.Read(ref _droppedControl),
        _ice.GetDiagnostics(), _dtls?.GetDiagnostics());
    private TimeSpan? Elapsed(long at) => at == 0 ? null : Stopwatch.GetElapsedTime(_startedAt, at);
    private void RequireOpen() { ObjectDisposedException.ThrowIf(_disposed || _state == PeerConnectionState.Closed, this); if (_state == PeerConnectionState.Failed) throw new InvalidOperationException("Peer connection failed."); }
    private void RequireState(PeerConnectionState state) { RequireOpen(); if (_state != state) throw new InvalidOperationException("Invalid initial signaling state."); }
    public override string ToString() => $"WebRTC peer {State} (signaling credentials redacted)";
}
