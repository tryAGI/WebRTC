using System.Diagnostics;
using System.Security.Authentication;

namespace tryAGI.WebRTC;

public enum EstablishmentPhase { Connection, Ice, Dtls, Sctp, Dcep }
public enum EstablishmentStatus { NotStarted, InProgress, Succeeded, Failed, Cancelled, NotNegotiated }
public enum EstablishmentFailure { None, Timeout, Cancelled, Disposed, Authentication, Protocol, Transport, Unknown }
public enum EstablishmentEventKind { Started, Progress, Retransmission, Succeeded, Failed, Cancelled, NotNegotiated }
/// <summary>Expected protocol boundary, never a peer-provided string or payload.</summary>
public enum HandshakeStep
{
    None, IceNomination, DtlsClientHello, DtlsServerHello, DtlsServerCertificate, DtlsServerKey,
    DtlsCertificateRequest, DtlsServerDone, DtlsClientCertificate, DtlsClientKey, DtlsCertificateVerify,
    DtlsFinished, DtlsComplete, SctpInit, SctpInitAck, SctpCookieEcho, SctpCookieAck, SctpComplete, DcepAck
}
public readonly record struct EstablishmentEvent(EstablishmentPhase Phase, EstablishmentEventKind Kind,
    HandshakeStep Step, EstablishmentFailure Failure, long TimestampTicks, long Operation);
public sealed record EstablishmentPhaseEvidence(EstablishmentPhase Phase, EstablishmentStatus Status,
    HandshakeStep Step, EstablishmentFailure Failure, TimeSpan? StartedAfter, TimeSpan? Elapsed,
    long Retransmissions, long Attempts, long Succeeded, long Failed, int Pending);
/// <summary>Version 1. Bounded establishment evidence and build identity retained after timeout/disposal.
/// TerminalState describes the peer before cleanup; public State may subsequently become Closed.
/// DCEP describes local OPEN attempts; Pending and operation numbers distinguish concurrent attempts.</summary>
public sealed record PeerEstablishmentEvidence(int Version, Guid PeerEpoch, long ClockAnchorTicks, long ClockFrequency,
    PeerConnectionState? TerminalState, EstablishmentFailure TerminalFailure, TimeSpan? Lifetime,
    DtlsRole? DtlsRole, SctpRole? SctpRole, IReadOnlyList<EstablishmentPhaseEvidence> Phases,
    IReadOnlyList<EstablishmentEvent> Events, long EventsOverwritten)
{
    /// <summary>Exact package and source inputs of the library collecting this evidence.</summary>
    public WebRtcBuildIdentity Build { get; init; } = WebRtcBuildInfo.Current;
    public long IceChecks { get; init; }
    public IReadOnlyList<IceDatagramRejectionCount> IceRejectedDatagrams { get; init; } = [];
    public long DtlsRejectedRecords { get; init; }
    public long SctpRejectedPackets { get; init; }
}

/// <summary>Only handshake/lifecycle boundaries enter this journal, never the media packet path.</summary>
internal sealed class EstablishmentJournal
{
    internal const int Capacity = 64;
    private readonly object _gate = new();
    private readonly long _anchor = Stopwatch.GetTimestamp();
    private readonly Phase[] _phases = Enumerable.Range(0, 5).Select(_ => new Phase()).ToArray();
    private readonly EstablishmentEvent[] _events = new EstablishmentEvent[Capacity];
    private int _read, _count;
    private long _overwritten, _ended;
    private PeerConnectionState? _terminal;
    private EstablishmentFailure _failure;
    private sealed class Phase
    {
        internal EstablishmentStatus Status;
        internal HandshakeStep Step;
        internal EstablishmentFailure Failure;
        internal long Started, Ended, Retransmissions, Attempts, Succeeded, Failed;
        internal int Pending;
    }
    internal static EstablishmentFailure Classify(Exception? error) => error switch
    {
        null => EstablishmentFailure.None,
        TimeoutException => EstablishmentFailure.Timeout,
        ObjectDisposedException => EstablishmentFailure.Disposed,
        OperationCanceledException => EstablishmentFailure.Cancelled,
        AuthenticationException => EstablishmentFailure.Authentication,
        InvalidDataException => EstablishmentFailure.Protocol,
        IOException or System.Net.Sockets.SocketException => EstablishmentFailure.Transport,
        _ => EstablishmentFailure.Unknown
    };
    internal long Begin(EstablishmentPhase phase, HandshakeStep step = HandshakeStep.None)
    {
        lock (_gate)
        {
            var p = _phases[(int)phase]; var now = Stopwatch.GetTimestamp();
            if (p.Pending == 0) { p.Started = now; p.Ended = 0; }
            p.Pending++; p.Attempts++; p.Status = EstablishmentStatus.InProgress; p.Step = step; p.Failure = EstablishmentFailure.None;
            Add(phase, EstablishmentEventKind.Started, step, EstablishmentFailure.None, now, p.Attempts); return p.Attempts;
        }
    }
    internal void Progress(EstablishmentPhase phase, HandshakeStep step, bool retransmission = false)
    {
        lock (_gate)
        {
            var p = _phases[(int)phase];
            if (p.Status != EstablishmentStatus.InProgress) return;
            if (retransmission) p.Retransmissions++;
            else if (p.Step == step) return;
            p.Step = step;
            Add(phase, retransmission ? EstablishmentEventKind.Retransmission : EstablishmentEventKind.Progress,
                step, EstablishmentFailure.None, Stopwatch.GetTimestamp(), p.Attempts);
        }
    }
    internal void End(EstablishmentPhase phase, Exception? error = null, long operation = 0)
    {
        lock (_gate)
        {
            var p = _phases[(int)phase];
            if (p.Pending == 0)
            {
                // A child sees linked-token cancellation first. The owner knows whether its
                // overall deadline, explicit caller cancellation or disposal caused it.
                if (p.Status == EstablishmentStatus.Cancelled && error is TimeoutException)
                {
                    p.Status = EstablishmentStatus.Failed; p.Failure = EstablishmentFailure.Timeout;
                    Add(phase, EstablishmentEventKind.Failed, p.Step, p.Failure, Stopwatch.GetTimestamp(), p.Attempts);
                }
                return;
            }
            var now = Stopwatch.GetTimestamp(); var failure = Classify(error); p.Pending--;
            if (error == null) p.Succeeded++; else p.Failed++;
            p.Failure = failure;
            p.Status = p.Pending > 0 ? EstablishmentStatus.InProgress : error == null ? EstablishmentStatus.Succeeded :
                failure is EstablishmentFailure.Cancelled or EstablishmentFailure.Disposed ? EstablishmentStatus.Cancelled : EstablishmentStatus.Failed;
            if (p.Pending == 0) p.Ended = now;
            Add(phase, error == null ? EstablishmentEventKind.Succeeded :
                failure is EstablishmentFailure.Cancelled or EstablishmentFailure.Disposed ? EstablishmentEventKind.Cancelled : EstablishmentEventKind.Failed,
                p.Step, failure, now, operation == 0 ? p.Attempts : operation);
        }
    }
    internal void NotNegotiated(EstablishmentPhase phase)
    {
        lock (_gate)
        {
            var p = _phases[(int)phase]; p.Status = EstablishmentStatus.NotNegotiated;
            Add(phase, EstablishmentEventKind.NotNegotiated, HandshakeStep.None, EstablishmentFailure.None, Stopwatch.GetTimestamp(), 0);
        }
    }
    internal void Terminal(PeerConnectionState state, Exception? error)
    {
        lock (_gate)
        {
            if (_terminal != null) return;
            _terminal = state; _failure = Classify(error); _ended = Stopwatch.GetTimestamp();
        }
    }
    private void Add(EstablishmentPhase phase, EstablishmentEventKind kind, HandshakeStep step, EstablishmentFailure failure, long now, long operation)
    {
        if (_count == Capacity) { _read = (_read + 1) % Capacity; _count--; _overwritten++; }
        _events[(_read + _count++) % Capacity] = new(phase, kind, step, failure, now, operation);
    }
    internal PeerEstablishmentEvidence Snapshot(Guid epoch, DtlsRole? dtls, SctpRole? sctp)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp(); var phases = new EstablishmentPhaseEvidence[5];
            for (var n = 0; n < phases.Length; n++)
            {
                var p = _phases[n];
                phases[n] = new((EstablishmentPhase)n, p.Status, p.Step, p.Failure,
                    p.Started == 0 ? null : Stopwatch.GetElapsedTime(_anchor, p.Started),
                    p.Started == 0 ? null : Stopwatch.GetElapsedTime(p.Started, p.Ended == 0 ? now : p.Ended),
                    p.Retransmissions, p.Attempts, p.Succeeded, p.Failed, p.Pending);
            }
            var events = new EstablishmentEvent[_count];
            for (var n = 0; n < events.Length; n++) events[n] = _events[(_read + n) % Capacity];
            return new(1, epoch, _anchor, Stopwatch.Frequency, _terminal, _failure,
                _ended == 0 ? null : Stopwatch.GetElapsedTime(_anchor, _ended), dtls, sctp,
                Array.AsReadOnly(phases), Array.AsReadOnly(events), _overwritten);
        }
    }
}

public sealed partial class PeerConnection
{
    private readonly EstablishmentJournal _establishment = new();
    /// <summary>Safe numeric snapshot available before connection, on failure and after disposal.
    /// Includes no SDP, endpoints, certificates, channel labels, exception messages or payloads.</summary>
    public PeerEstablishmentEvidence GetEstablishmentEvidence() => _establishment.Snapshot(_diagnosticEpoch,
        _dtls?.Role, _sctp?.Role) with
    {
        IceChecks = _ice.GetDiagnostics().SentChecks,
        IceRejectedDatagrams = _ice.GetDatagramRejectionCounts(),
        DtlsRejectedRecords = _dtls?.GetDiagnostics().RejectedRecords ?? 0,
        SctpRejectedPackets = _sctp?.GetDiagnostics().RejectedPackets ?? 0
    };
}
