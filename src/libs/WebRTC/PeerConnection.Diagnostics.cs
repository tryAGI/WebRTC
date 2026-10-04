using System.Diagnostics;
using System.Net;

namespace tryAGI.WebRTC;

public sealed record PeerRouteEvidence(int PathGeneration, IceCandidateType? LocalCandidateType, IceCandidateType? RemoteCandidateType,
    DiagnosticPath? Path, TimeSpan? IceCheckRoundTripTime, TimeSpan? IceCheckRoundTripTimeAge, bool IceCheckRoundTripTimeStale, TimeSpan? ConsentAge, TimeSpan? RtcpRoundTripTime,
    TimeSpan? RtcpRoundTripTimeAge, bool RtcpRoundTripTimeStale, TimeSpan? LastAuthenticatedMediaAge,
    TimeSpan? FirstMediaTime, bool PictureLossNegotiated, long PictureLossSent, long PictureLossReceived,
    bool RetransmissionNegotiated, bool AudioRecoveryObservable, IPEndPoint? LocalEndPoint, IPEndPoint? RemoteEndPoint);
public sealed record RtpSourceEvidence(Guid StreamEpoch, PacketDirection Direction, uint? Source, int ClockRate, long ResetEpoch,
    long SampleTicks, TimeSpan Age, bool Ready, byte FractionLost, int CumulativeLost, uint HighestSequence,
    uint JitterRtpTicks, long ReceivedPackets, PacketReason LastReason);
public sealed record RemoteReceptionEvidence(Guid StreamEpoch, PacketDirection Direction, uint? Source, long SampleTicks, TimeSpan Age,
    byte FractionLost, int CumulativeLost, uint HighestSequence, uint JitterRtpTicks, int ClockRate);
public sealed record QueueEvidence(PacketStage EnqueueBoundary, DiagnosticPath? Path, int Depth, int? ObservedHighWater, TimeSpan? OldestAge);
public sealed record PeerRtpEvidence(IReadOnlyList<RtpSourceEvidence> ReceivedStreams,
    IReadOnlyList<RemoteReceptionEvidence> RemoteReportsAboutTransmittedStreams);

public sealed partial class PeerConnection
{
    /// <summary>Live capture; replaces previous capture without restarting transport. Cancellation/expiry affect diagnostics only.</summary>
    public PeerDiagnosticSession AttachDiagnostics(PeerDiagnosticsOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        lock (_gate)
        {
            RequireOpen();
            var capture = new PeerDiagnosticSession(_diagnosticEpoch, options, cancellationToken) { ConnectionStartedAt = _startedAt };
            var previous = Interlocked.Exchange(ref _diagnostics, capture);
            Volatile.Write(ref _ice.Diagnostics, capture); previous?.Dispose(); return capture;
        }
    }
    public void DetachDiagnostics()
    {
        lock (_gate)
        {
            Volatile.Write(ref _ice.Diagnostics, null);
            Interlocked.Exchange(ref _diagnostics, null)?.Dispose();
        }
    }
    /// <summary>Sensitive identity opt-in; snapshot reads never advance RTCP reporting intervals.</summary>
    public PeerRtpEvidence GetRtpEvidence(bool includePacketIdentifiers = false) => _rtcp?.Evidence(includePacketIdentifiers) ?? new([], []);
    public IReadOnlyList<QueueEvidence> GetQueueEvidence()
    {
        var capture = Volatile.Read(ref _diagnostics);
        var audioAt = _audio.Reader.TryPeek(out var audio) ? audio.Trace.LastStageTicks : 0;
        var entries = new List<QueueEvidence>
        {
            new(PacketStage.AudioEnqueued, null, _audio.Reader.Count, capture?.QueueHighWater(PacketStage.AudioEnqueued),
                audioAt == 0 ? null : Stopwatch.GetElapsedTime(audioAt)),
        };
        entries.AddRange(_ice.QueueEvidence());
        if (_dtls != null) entries.Add(_dtls.QueueEvidence(capture));
        return entries.AsReadOnly();
    }
    public PeerRouteEvidence GetRouteEvidence(bool includeEndPoints = false)
    {
        var ice = _ice.GetDiagnostics(); var rtcp = _rtcp?.RttEvidence();
        var authenticated = _dtls?.LastAuthenticatedAt ?? 0;
        return new(_ice.DiagnosticGeneration, ice.SelectedLocalCandidateType, ice.SelectedRemoteCandidateType,
            ice.SelectedLocalCandidateType == null ? null : _ice.SelectedDiagnosticPath, ice.LastCheckRoundTripTime, ice.CheckRoundTripTimeAge,
            !_ice.IsConnected || ice.CheckRoundTripTimeAge == null || ice.CheckRoundTripTimeAge > _options.Ice.ConsentTimeout, ice.ConsentAge, rtcp?.Value, rtcp?.Age, rtcp == null || rtcp.Value.Stale,
            authenticated == 0 ? null : Stopwatch.GetElapsedTime(authenticated), Elapsed(_dtls?.FirstAuthenticatedRtpAt ?? 0),
            _session?.VideoPictureLoss == true, _rtcp?.Diagnostics().SentPictureLoss ?? 0, _rtcp?.Diagnostics().ReceivedPictureLoss ?? 0,
            false, false, includeEndPoints ? ice.SelectedLocalEndPoint : null, includeEndPoints ? ice.SelectedRemoteEndPoint : null);
    }
}
