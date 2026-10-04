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
    public IReadOnlyList<VideoCodecCapability> VideoCodecs { get; init; } = [];
    public SdpDirection VideoDirection { get; init; } = SdpDirection.ReceiveOnly;
    public PeerVideoOptions Video { get; init; } = new();
    public PeerRtcpOptions Rtcp { get; init; } = new();
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

/// <summary>Owns initial Opus/video/data BUNDLE and bounded resolved STUN/TURN paths. Video codecs are externally supplied; no decoding or jitter buffer.</summary>
public sealed partial class PeerConnection : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PeerConnectionOptions _options;
    private readonly IceUdpTransport _ice;
    private readonly DtlsIdentity _identity;
    private readonly IPEndPoint _endpoint;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _videoSend = new(1, 1);
    private readonly SemaphoreSlim _audioSend = new(1, 1);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mediaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dataReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<QueuedAudio> _audio;
    private readonly Guid _diagnosticEpoch = Guid.NewGuid();
    private PeerDiagnosticSession? _diagnostics;
    private readonly record struct QueuedAudio(EncodedOpusPacket Packet, PacketDiagnostic Trace);
    private long _lastAudioSendAt;
    private uint _lastAudioTimestamp;
    private readonly Channel<EncodedVideoFrame> _video;
    private readonly Channel<byte[]> _control;
    private PeerConnectionState _state;
    private SdpSessionDescription? _local, _remote;
    private SdpNegotiatedSession? _session;
    private PeerAudio? _routing;
    private PeerVideo? _videoRouting;
    private PeerRtcp? _rtcp;
    public string CanonicalName { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private DtlsSrtpTransport? _dtls;
    private SctpAssociation? _sctp;
    private DataChannelAssociation? _channels;
    private Task? _run, _dispose;
    private bool _disposed;
    private readonly List<IceCandidate> _localCandidates = [];
    private int _activeGathering;
    private bool _gatheringComplete;
    private ushort _videoSequence = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
    private long _droppedVideo;
    private ushort _sequence = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
    private long _startedAt, _mediaAt, _connectedAt, _rejectedAudio, _rejectedControl, _droppedAudio, _droppedControl;
    public uint AudioSource { get; }
    public uint VideoSource { get; }
    public SdpVideoFormat? VideoFormat { get { lock (_gate) return _session?.VideoFormat; } }
    public bool CanSendVideo { get { lock (_gate) return _session?.CanSendVideo == true; } }
    public int MaximumVideoPayloadBytes { get { lock (_gate) return _videoRouting == null ? 0 : _options.Dtls.MaximumDatagramSize - _videoRouting.HeaderLength - 16; } }
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
        PeerVideo.Validate(options.Video);
        PeerRtcp.Validate(options.Rtcp, options.Dtls.MaximumDatagramSize);
        ArgumentNullException.ThrowIfNull(options.VideoCodecs);
        var videoCodecs = options.VideoCodecs.Take(9).ToArray();
        _ = SdpNegotiation.VideoCapabilities(videoCodecs);
        if (!Enum.IsDefined(options.VideoDirection)) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options with { VideoCodecs = Array.AsReadOnly(videoCodecs) }; _identity = DtlsIdentity.Generate();
        try { _ice = new(new(options.LocalEndPoint.Address, options.LocalEndPoint.Port), options: options.Ice with
            { RemoteCandidateFilter = SupportedCandidate }) { Establishment = _establishment }; }
        catch { _identity.Dispose(); throw; }
        _endpoint = _ice.LocalEndPoint;
        do { AudioSource = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)); } while (AudioSource == 0);
        do { VideoSource = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)); } while (VideoSource == 0 || VideoSource == AudioSource);
        _video = Channel.CreateBounded<EncodedVideoFrame>(new BoundedChannelOptions(options.Video.QueueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true });
        _audio = Channel.CreateBounded<QueuedAudio>(new BoundedChannelOptions(options.AudioQueueCapacity)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true }, dropped => { Interlocked.Increment(ref _droppedAudio); var trace = dropped.Trace; trace.Mark(PacketStage.Dropped, PacketReason.QueueOverflow); });
        _control = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.ControlQueueCapacity)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true }, _ => Interlocked.Increment(ref _droppedControl));
    }
    private SdpLocalTransport LocalTransport() => new(_ice.LocalCredentials, _identity.GetFingerprintSha256(), _endpoint,
        _options.Sctp.LocalPort, Math.Min(_options.Sctp.MaximumMessageSize, _options.Channels.ReceiveBufferBytes), _localCandidates, _gatheringComplete, true, _options.Ice.RelayOnly, CanonicalName);
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
        var gatheringAt = Stopwatch.GetTimestamp();
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
        finally { lock (_gate) _activeGathering--; Volatile.Read(ref _diagnostics)?.Lifecycle(PacketStage.Gathering, gatheringAt); }
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
            var text = SdpNegotiation.CreateOffer(LocalTransport(), AudioSource, VideoSource, _options.VideoCodecs, _options.DataChannels, _options.AudioDirection, _options.VideoDirection);
            _local = SdpSessionDescription.Parse(text); LocalDescription = text; _state = PeerConnectionState.HaveLocalOffer; return text;
        }
    }
    public string CreateAnswer(string remoteOffer, SdpSetup preferredSetup = SdpSetup.Active)
    {
        var remote = SdpSessionDescription.Parse(remoteOffer);
        lock (_gate)
        {
            RequireState(PeerConnectionState.New);
            var text = SdpNegotiation.CreateAnswer(remote, LocalTransport(), AudioSource, VideoSource, _options.VideoCodecs, _options.DataChannels, preferredSetup, _options.AudioDirection, _options.VideoDirection);
            var local = SdpSessionDescription.Parse(text);
            var session = SdpNegotiation.ValidateAnswer(remote, local, false);
            var routing = new PeerAudio(session, remote, AudioSource, _options.MaximumAudioSources);
            _videoRouting = new(session, remote, VideoSource, AudioSource, _options.Video);
            _rtcp = session.LocalAudio == null && session.LocalVideo == null ? null :
                new(session, _videoRouting, AudioSource, VideoSource, CanonicalName, _options.Rtcp, _options.Dtls.MaximumDatagramSize);
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
            var session = SdpNegotiation.ValidateAnswer(_local!, remote, true);
            var routing = new PeerAudio(session, remote, AudioSource, _options.MaximumAudioSources);
            _videoRouting = new(session, remote, VideoSource, AudioSource, _options.Video);
            _rtcp = session.LocalAudio == null && session.LocalVideo == null ? null :
                new(session, _videoRouting, AudioSource, VideoSource, CanonicalName, _options.Rtcp, _options.Dtls.MaximumDatagramSize);
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
            Volatile.Read(ref _diagnostics)?.Lifecycle(PacketStage.Connection, 0);
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
        var diagnostic = Volatile.Read(ref _diagnostics);
        var trace = diagnostic?.Begin(PacketDirection.Send, _ice.SelectedDiagnosticPath, _ice.DiagnosticGeneration, payload.Length) ?? default;
        trace.Protocol(DiagnosticProtocol.AudioRtp);
        trace.Mark(PacketStage.CallerSubmission);
        try { await _audioSend.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { trace.Mark(PacketStage.Dropped, PacketReason.Cancelled); throw; }
        try
        {
            lock (_gate) RequireOpen();
            if (!_mediaReady.Task.IsCompletedSuccessfully || !_session!.CanSendAudio) throw new InvalidOperationException("Sending Opus was not negotiated or media is not ready.");
            if (payload.Length < 1 || payload.Length > MaximumAudioPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload));
            trace.Identify(AudioSource, _sequence, rtpTimestamp, 1);
            var mediaDelta = unchecked((int)(rtpTimestamp - _lastAudioTimestamp));
            var sendAt = trace.Owner == null ? 0 : Stopwatch.GetTimestamp();
            var catchUp = sendAt != 0 && _lastAudioSendAt != 0 && sendAt >= _lastAudioSendAt && mediaDelta > 0 &&
                Stopwatch.GetElapsedTime(_lastAudioSendAt, sendAt).TotalSeconds < mediaDelta / 48000.0 / 2;
            trace.Mark(PacketStage.SendLockAcquired, catchUp ? PacketReason.CatchUpBurst : PacketReason.None);
            _lastAudioSendAt = sendAt; _lastAudioTimestamp = rtpTimestamp;
            var packet = _routing!.Write(payload.Span, _sequence++, rtpTimestamp, marker);
            // Consume the sequence even if transmission is cancelled after protection; SRTP indexes cannot be reused.
            await _dtls!.SendDiagnosticRtpAsync(packet, trace, linked.Token).ConfigureAwait(false);
            _rtcp!.SentRtp(false, rtpTimestamp, payload.Length);
        }
        finally { _audioSend.Release(); }
    }
    /// <summary>Sends one externally packetized negotiated H264/VP8 fragment. Caller owns encoding, frame boundaries, timing and congestion control.</summary>
    public async ValueTask SendVideoRtpAsync(ReadOnlyMemory<byte> payload, uint rtpTimestamp, bool marker = false, CancellationToken cancellationToken = default)
    {
        lock (_gate) RequireOpen();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var trace = Volatile.Read(ref _diagnostics)?.Begin(PacketDirection.Send, _ice.SelectedDiagnosticPath, _ice.DiagnosticGeneration, payload.Length) ?? default;
        trace.Protocol(DiagnosticProtocol.VideoRtp); trace.Mark(PacketStage.CallerSubmission);
        try { await _videoSend.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { trace.Mark(PacketStage.Dropped, PacketReason.Cancelled); throw; }
        try
        {
            lock (_gate) RequireOpen();
            if (!_mediaReady.Task.IsCompletedSuccessfully || !_session!.CanSendVideo) throw new InvalidOperationException("Sending video was not negotiated or media is not ready.");
            if (payload.Length < 1 || payload.Length > MaximumVideoPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload));
            trace.Identify(VideoSource, _videoSequence, rtpTimestamp, 1); trace.Mark(PacketStage.SendLockAcquired);
            var packet = _videoRouting!.Write(payload.Span, _videoSequence++, rtpTimestamp, marker);
            await _dtls!.SendDiagnosticRtpAsync(packet, trace, linked.Token).ConfigureAwait(false);
            _rtcp!.SentRtp(true, rtpTimestamp, payload.Length);
        }
        finally { _videoSend.Release(); }
    }
    public IAsyncEnumerable<EncodedVideoFrame> ReceiveVideoAsync(CancellationToken cancellationToken = default) => _video.Reader.ReadAllAsync(cancellationToken);
    public PeerVideoDiagnostics GetVideoDiagnostics() => _videoRouting?.Diagnostics(Interlocked.Read(ref _droppedVideo)) ?? new(0, 0, 0, 0, 0, 0, 0);
    public async IAsyncEnumerable<EncodedOpusPacket> ReceiveAudioAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var item in _audio.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var trace = item.Trace; trace.Mark(PacketStage.AudioDequeued, queueDepth: _audio.Reader.Count);
                trace.Mark(PacketStage.ConsumerDelivery); yield return item.Packet;
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) Volatile.Read(ref _diagnostics)?.Lifecycle(PacketStage.Shutdown, 0, PacketReason.Cancelled);
        }
    }
    public IAsyncEnumerable<byte[]> ReceiveRtcpAsync(CancellationToken cancellationToken = default) => _control.Reader.ReadAllAsync(cancellationToken);
    /// <summary>Advanced SR/RR/CNAME sending through a bounded shared RTCP budget. Caller owns report fields and regular-report timing.
    /// Feedback must use RequestVideoKeyFrame so it cannot bypass source, retry or scheduling limits.</summary>
    public async ValueTask SendRtcpAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        PeerRtcp control;
        lock (_gate)
        {
            RequireOpen();
            if (!_mediaReady.Task.IsCompletedSuccessfully) throw new InvalidOperationException("Secure media is not ready.");
            control = _rtcp ?? throw new NotSupportedException("RTCP media was not negotiated.");
        }
        if (packet.Length is < 8 or > 1200) throw new ArgumentException("Invalid RTCP packet size.", nameof(packet));
        var owned = packet.ToArray(); // Snapshot before the first await; the caller may reuse its buffer.
        if (!control.ValidateOutgoing(owned)) throw new ArgumentException("RTCP requires admitted local reports and negotiated framing; use RequestVideoKeyFrame for feedback.", nameof(packet));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await control.SendExternalAsync(owned, linked.Token).ConfigureAwait(false);
    }
    /// <summary>Requests a refresh for an announced or admitted remote video source. PLI must have been negotiated; requests are coalesced, scheduled and bounded.</summary>
    public void RequestVideoKeyFrame(uint source)
    {
        lock (_gate) { RequireOpen(); if (_rtcp == null) throw new NotSupportedException("RTCP media was not negotiated."); _rtcp.RequestPictureLoss(source); }
    }
    /// <summary>Bounded encoder notifications for admitted remote PLI targeting this peer's negotiated local video source. Application owns encoding.</summary>
    public IAsyncEnumerable<VideoKeyFrameRequest> ReceiveVideoKeyFrameRequestsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RequireOpen();
            if (_rtcp == null || _session?.CanSendVideo != true || !_session.VideoPictureLoss) throw new NotSupportedException("Sending video with picture-loss feedback was not negotiated.");
            return _rtcp.KeyFrameRequests(cancellationToken);
        }
    }
    public PeerRtcpDiagnostics? GetRtcpDiagnostics() => _rtcp?.Diagnostics();
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
        Exception? reason = null; Task? media = null, videoExpiry = null, control = null;
        using var establishment = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        establishment.CancelAfter(_options.ConnectionTimeout);
        _establishment.Begin(EstablishmentPhase.Connection);
        _establishment.Begin(EstablishmentPhase.Ice, HandshakeStep.IceNomination);
        try
        {
            await _ice.ConnectAsync(_session!.RemoteCredentials, _session.IceRole, candidates, establishment.Token).ConfigureAwait(false);
            _establishment.End(EstablishmentPhase.Ice);
            _dtls = new(_ice, _identity, _session.DtlsRole, Convert.FromHexString(_session.RemoteFingerprintSha256), _options.Dtls) { Establishment = _establishment };
            var dtlsAt = Stopwatch.GetTimestamp();
            await _dtls.ConnectAsync(establishment.Token).ConfigureAwait(false);
            Volatile.Read(ref _diagnostics)?.Lifecycle(PacketStage.Dtls, dtlsAt);
            Interlocked.Exchange(ref _mediaAt, Stopwatch.GetTimestamp()); _mediaReady.TrySetResult();
            control = _rtcp?.RunAsync(_dtls, _lifetime.Token) ?? Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token);
            media = ReceiveMediaAsync(); videoExpiry = _session.CanReceiveVideo ? ExpireVideoAsync() : Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token);
            if (_session.LocalData != null)
            {
                var sctpOptions = _options.Sctp with { LocalPort = _session.LocalData.SctpPort!.Value, RemotePort = _session.RemoteData!.SctpPort!.Value,
                    MaximumMessageSize = _session.MaximumMessageSize, MaximumPacketSize = Math.Min(_options.Sctp.MaximumPacketSize, _dtls.MaximumApplicationDatagramSize) };
                // RFC 8841 section 9.3: both SCTP endpoints initiate, independently of DTLS setup.
                _sctp = new(_dtls, SctpRole.Initiator, sctpOptions) { Establishment = _establishment };
                var sctpAt = Stopwatch.GetTimestamp();
                await _sctp.ConnectAsync(establishment.Token).ConfigureAwait(false);
                Volatile.Read(ref _diagnostics)?.Lifecycle(PacketStage.Sctp, sctpAt);
                _channels = new(_sctp, _options.Channels) { Establishment = _establishment }; _dataReady.TrySetResult();
            }
            else
            {
                _establishment.NotNegotiated(EstablishmentPhase.Sctp); _establishment.NotNegotiated(EstablishmentPhase.Dcep);
                _dataReady.TrySetException(new NotSupportedException("Data channels were not negotiated."));
            }
            establishment.Token.ThrowIfCancellationRequested();
            lock (_gate) { RequireOpen(); _state = PeerConnectionState.Connected; }
            Interlocked.Exchange(ref _connectedAt, Stopwatch.GetTimestamp());
            Volatile.Read(ref _diagnostics)?.Lifecycle(PacketStage.Connection, _startedAt);
            _establishment.End(EstablishmentPhase.Connection); _connected.TrySetResult();
            establishment.Dispose();
            // Caller cancellation applies to establishment only, including a stalled SCTP handshake.
            var ends = new List<Task> { _ice.Completion, _dtls.Completion, media, videoExpiry, control, Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token) };
            if (_channels != null) ends.Add(_channels.Completion);
            await Task.WhenAny(ends).ConfigureAwait(false);
            if (_channels?.Completion.IsCompleted == true) reason = await _channels.Completion.ConfigureAwait(false);
            else if (_dtls.Completion.IsCompleted) reason = await _dtls.Completion.ConfigureAwait(false);
            else if (_ice.Completion.IsCompleted) reason = await _ice.Completion.ConfigureAwait(false);
            else if (control.IsCompleted) { await control.ConfigureAwait(false); reason = new IOException("RTCP worker ended unexpectedly."); }
            else if (videoExpiry.IsCompleted) { await videoExpiry.ConfigureAwait(false); reason = new IOException("Video expiry task ended unexpectedly."); }
            else if (media.IsCompleted) { await media.ConfigureAwait(false); reason = new IOException("Secure media stream ended."); }
        }
        catch (Exception error)
        {
            reason = error is OperationCanceledException && establishment.IsCancellationRequested &&
                !caller.IsCancellationRequested && !_lifetime.IsCancellationRequested
                ? new TimeoutException("Peer establishment deadline expired.", error) : error;
        }
        finally
        {
            // Freeze the original outcome before child disposal or public Closed state can erase it.
            _establishment.End(EstablishmentPhase.Ice, reason);
            _establishment.End(EstablishmentPhase.Dtls, reason);
            _establishment.End(EstablishmentPhase.Sctp, reason);
            _establishment.End(EstablishmentPhase.Connection, reason);
            var intentionalCancellation = _disposed && reason is OperationCanceledException;
            _establishment.Terminal(reason == null || intentionalCancellation ? PeerConnectionState.Closed : PeerConnectionState.Failed, intentionalCancellation ? null : reason);
            _lifetime.Cancel();
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception error) { reason ??= error; }
            if (media != null) { try { await media.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception error) { reason ??= error; } }
            if (videoExpiry != null) { try { await videoExpiry.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception error) { reason ??= error; } }
            if (control != null) { try { await control.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception error) { reason ??= error; } }
            _videoRouting?.Dispose();
            Finish(reason);
        }
    }
    private async Task ReceiveMediaAsync()
    {
        await foreach (var datagram in _dtls!.ReceiveMediaDatagramsAsync(_lifetime.Token).ConfigureAwait(false))
        {
            if (datagram.Kind == SecureMediaKind.Rtp)
            {
                if (_session!.VideoFormat != null && RtpPacket.TryParse(datagram.Data, out var rtp) && rtp.PayloadType == _session.VideoFormat.PayloadType)
                {
                    if (_routing!.HasSource(rtp.SynchronizationSource)) _videoRouting!.RejectSource();
                    else
                    {
                        if (_videoRouting!.Read(datagram.Data, out var frames, out var refreshChanged))
                        {
                            _rtcp!.ReceivedRtp(rtp.SynchronizationSource, rtp.SequenceNumber, rtp.Timestamp, true);
                            foreach (var frame in frames) EnqueueVideo(frame);
                        }
                        if (refreshChanged) _rtcp!.Wake();
                    }
                }
                else
                {
                    var collision = RtpPacket.TryParse(datagram.Data, out var audioRtp) && _videoRouting!.HasSource(audioRtp.SynchronizationSource);
                    var packet = collision ? null : _routing!.Read(datagram.Data);
                    var trace = datagram.Trace; trace.Protocol(DiagnosticProtocol.AudioRtp);
                    if (packet == null) { Interlocked.Increment(ref _rejectedAudio); trace.Mark(PacketStage.Dropped, PacketReason.SourceOrNegotiation); }
                    else
                    {
                        var sourceState = _rtcp!.ReceivedRtp(packet.SynchronizationSource, packet.SequenceNumber, packet.Timestamp, false, trace.Owner != null);
                        if (sourceState != null) trace.Identify(packet.SynchronizationSource, packet.SequenceNumber, packet.Timestamp, sourceState.Value.ResetEpoch);
                        trace.Mark(PacketStage.AudioEnqueued, sourceState?.LastReason ?? PacketReason.None,
                            queueDepth: Math.Min(_options.AudioQueueCapacity, _audio.Reader.Count + 1));
                        _audio.Writer.TryWrite(new(packet, trace));
                    }
                }
            }
            else if (_rtcp == null || !_rtcp.Read(datagram.Data)) Interlocked.Increment(ref _rejectedControl);
            else _control.Writer.TryWrite(datagram.Data);
        }
    }
    private void EnqueueVideo(EncodedVideoFrame frame)
    {
        // One producer owns eviction. A queued fresh key frame for the same source supersedes the dropped reference.
        while (!_video.Writer.TryWrite(frame))
        {
            if (_video.Reader.TryRead(out var dropped))
            {
                Interlocked.Increment(ref _droppedVideo);
                if ((!frame.IsKeyFrame || frame.SynchronizationSource != dropped.SynchronizationSource) && _videoRouting!.QueueLoss(dropped.SynchronizationSource)) _rtcp!.Wake();
            }
            else if (_lifetime.IsCancellationRequested) return;
        }
    }
    private async Task ExpireVideoAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false)) { if (_videoRouting!.Expire()) _rtcp!.Wake(); }
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
        Volatile.Read(ref _diagnostics)?.Lifecycle(PacketStage.Shutdown, _startedAt, reason is OperationCanceledException ? PacketReason.Cancelled : PacketReason.Shutdown);
        lock (_gate) { if (_disposed) reason = null; _state = reason == null ? PeerConnectionState.Closed : PeerConnectionState.Failed; }
        var failure = reason ?? new ObjectDisposedException(nameof(PeerConnection));
        _connected.TrySetException(failure); _mediaReady.TrySetException(failure); _dataReady.TrySetException(failure);
        _rtcp?.Complete();
        _videoRouting?.Dispose(); _video.Writer.TryComplete(reason);
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
        else { _establishment.Terminal(PeerConnectionState.Closed, null); await CleanupAsync().ConfigureAwait(false); Finish(null); }
        lock (_gate) _state = PeerConnectionState.Closed;
        DetachDiagnostics();
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
