using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace tryAGI.WebRTC;

public enum PacketDirection { Receive, Send }
public enum DiagnosticProtocol { Transport, Rtp, AudioRtp, VideoRtp, Rtcp, Dtls, Sctp }
public enum DiagnosticPath { HostUdp, TurnUdp, TurnTcp, TurnTls }
public enum PacketStage
{
    ReceiveArmed, ManagedReceiveCompleted, StreamReadCompleted, RelayFrameCompleted,
    Demultiplexed, RelayEnqueued, RelayDequeued, IceEnqueued, IceDequeued, AuthenticationStarted, Authenticated,
    SecureMediaEnqueued, SecureMediaDequeued, AudioEnqueued, AudioDequeued, ConsumerDelivery,
    ReceiveHandlerCompleted, ReceiveRearm, CallerSubmission, SendLockAcquired, SecureSendLockAcquired, TurnSendLockAcquired,
    ProtectionLockAcquired, ProtectionStarted, Protected, SocketSendStarted, SocketSendCompleted, Dropped,
    Gathering, Connection, Ice, Dtls, Sctp, FirstMedia, Consent, Shutdown
}
public enum PacketReason
{
    None, QueueOverflow, InvalidRouteOrConsent, InvalidFraming, Authentication, ReplayOrTooOld,
    SourceOrNegotiation, Reordered, Duplicate, Cancelled, Shutdown, SendFailure, CatchUpBurst
}
public enum DiagnosticCaptureState { Active, Expired, Cancelled, Disposed }

/// <summary>Immutable policy; hard caps include both capture and deferred metric samples. No payload/endpoint capture.</summary>
public sealed record PeerDiagnosticsOptions
{
    public bool Metrics { get; init; } = true;
    public bool PacketTrace { get; init; }
    public bool IncludePacketIdentifiers { get; init; }
    public int SampleEvery { get; init; } = 1;
    public int EventCapacity { get; init; } = 2048;
    public int MaximumBufferBytes { get; init; } = 1024 * 1024;
    public TimeSpan CaptureDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AnomalyThreshold { get; init; } = TimeSpan.FromMilliseconds(100);
    internal void Validate()
    {
        if (SampleEvery is < 1 or > 10000 || EventCapacity is < 8 or > 8192 || MaximumBufferBytes is < 32768 or > 4 * 1024 * 1024 ||
            CaptureDuration < TimeSpan.FromMilliseconds(10) || CaptureDuration > TimeSpan.FromMinutes(5) ||
            AnomalyThreshold < TimeSpan.FromMilliseconds(1) || AnomalyThreshold > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(PeerDiagnosticsOptions));
    }
}

/// <summary>Version 1, Stopwatch clock only. DurationTicks describes this stage's preceding boundary, not wire latency.
/// PacketId is capture-local; identifiers are null unless explicitly enabled and authenticated (or locally transmitted).</summary>
public readonly record struct PacketStageEvent(int Version, long PacketId, int PathGeneration, DiagnosticPath Path,
    PacketDirection Direction, PacketStage Stage, PacketReason Reason, long TimestampTicks, long DurationTicks,
    int Bytes, int QueueDepth, uint? Source, ushort? Sequence, uint? RtpTimestamp, long? SourceEpoch, DiagnosticProtocol Protocol = DiagnosticProtocol.Transport, PacketStage? PreviousStage = null);
public sealed record DiagnosticClock(long AnchorTicks, long Frequency, DateTimeOffset ApproximateUtcAnchor,
    bool KernelReceiveTimestampSupported = false, bool SocketOverflowCounterSupported = false);
public sealed record PeerDiagnosticSnapshot(Guid PeerEpoch, Guid CaptureEpoch, DiagnosticClock Clock, DiagnosticCaptureState State,
    int BufferedTraceEvents, int BufferedMetricEvents, long TraceEventsDropped, long MetricEventsDropped,
    long CollectorFailures, int AudioQueueHighWater, long RecordedEvents, long? LastAuthenticatedTicks);

/// <summary>Live opt-in capture. Transport writes bounded numeric records only. Drain/Publish run on the consumer's thread.
/// No exporter, background task, callback, I/O or packet Activity is invoked by transport instrumentation.</summary>
public sealed class PeerDiagnosticSession : IDisposable
{
    public const string InstrumentationName = "tryAGI.WebRTC";
    public const int ContractVersion = 1;
    // Conservative record budget (value-only event is smaller); two rings are accounted together.
    public const int EventBudgetBytes = 160;
    private readonly object _bufferGate = new(), _publishGate = new();
    private readonly PacketStageEvent[] _trace, _metrics, _publishSamples;
    private readonly long[] _counts = new long[2 * StageCount * ReasonCount];
    private readonly long[] _published = new long[2 * StageCount * ReasonCount];
    public const int FixedCounterBudgetBytes = 2 * ((int)PacketStage.Shutdown + 1) * ((int)PacketReason.CatchUpBurst + 1) * sizeof(long) * 2 + ((int)PacketStage.Shutdown + 1) * sizeof(int);
    private const int StageCount = (int)PacketStage.Shutdown + 1, ReasonCount = (int)PacketReason.CatchUpBurst + 1;
    private readonly int[] _queueHighWater = new int[StageCount];
    private int _traceRead, _traceCount, _metricRead, _metricCount, _state, _audioHighWater;
    private long _traceDropped, _metricDropped, _collectorFailures, _recorded, _packetId, _lastAuthenticated;
    private readonly long _expires;
    internal long ConnectionStartedAt;
    private readonly PeerDiagnosticsOptions _options;
    private CancellationTokenRegistration _cancellation;
    // Created only at the explicit consumer publication boundary; one bounded, process-lifetime set.
    private static readonly Lazy<PublicationInstruments> Instruments = new(() => new());
    private sealed class PublicationInstruments
    {
        internal readonly Meter Meter = new(InstrumentationName, "1.0");
        internal readonly ActivitySource Activities = new(InstrumentationName, "1.0");
        internal readonly Counter<long> Events;
        internal readonly Histogram<double> Duration;
        internal readonly Histogram<int> Queue;
        internal readonly Counter<long> Drops;
        internal PublicationInstruments()
        {
            Events = Meter.CreateCounter<long>("webrtc.stage.events", "{event}");
            Duration = Meter.CreateHistogram<double>("webrtc.stage.duration", "ms");
            Queue = Meter.CreateHistogram<int>("webrtc.queue.depth", "{packet}");
            Drops = Meter.CreateCounter<long>("webrtc.diagnostics.dropped", "{event}");
        }
    }
    private ActivitySource? _activities;
    private Counter<long>? _events;
    private Histogram<double>? _duration;
    private Histogram<int>? _queue;
    private Counter<long>? _collectionDrops;
    private long _publishedDrops;
    private bool _publishing;
    public Guid PeerEpoch { get; }
    public Guid CaptureEpoch { get; } = Guid.NewGuid();
    public DiagnosticClock Clock { get; }
    public int TraceCapacity => _trace.Length;
    public int MetricCapacity => _metrics.Length;
    public int BufferBudgetBytes => FixedCounterBudgetBytes + (TraceCapacity + MetricCapacity + _publishSamples.Length) * EventBudgetBytes;
    internal PeerDiagnosticSession(Guid epoch, PeerDiagnosticsOptions options, CancellationToken cancellation)
    {
        options.Validate(); _options = options; PeerEpoch = epoch;
        var now = Stopwatch.GetTimestamp(); Clock = new(now, Stopwatch.Frequency, DateTimeOffset.UtcNow);
        _expires = now + (long)(options.CaptureDuration.TotalSeconds * Stopwatch.Frequency);
        var rings = (options.PacketTrace ? 1 : 0) + (options.Metrics ? 2 : 0);
        var capacity = rings == 0 ? 0 : Math.Min(options.EventCapacity, (options.MaximumBufferBytes - FixedCounterBudgetBytes) / EventBudgetBytes / rings);
        _trace = options.PacketTrace ? new PacketStageEvent[capacity] : [];
        _metrics = options.Metrics ? new PacketStageEvent[capacity] : [];
        _publishSamples = options.Metrics ? new PacketStageEvent[capacity] : [];
        _cancellation = cancellation.UnsafeRegister(static state => ((PeerDiagnosticSession)state!).Stop(DiagnosticCaptureState.Cancelled), this);
    }
    private bool Active(long now)
    {
        if (Volatile.Read(ref _state) != 0) return false;
        if (now < _expires) return true;
        Interlocked.CompareExchange(ref _state, (int)DiagnosticCaptureState.Expired, 0); return false;
    }
    internal PacketDiagnostic Begin(PacketDirection direction, DiagnosticPath path, int generation, int bytes)
    {
        if (!_options.Metrics && !_options.PacketTrace || !Active(Stopwatch.GetTimestamp())) return default;
        return new(this, Interlocked.Increment(ref _packetId), direction, path, generation, bytes);
    }
    internal void Record(in PacketStageEvent value)
    {
        if (!_options.Metrics && !_options.PacketTrace || !Active(value.TimestampTicks)) return;
        Interlocked.Increment(ref _counts[((int)value.Direction * StageCount + (int)value.Stage) * ReasonCount + (int)value.Reason]);
        Interlocked.Increment(ref _recorded);
        if (value.Stage == PacketStage.Authenticated) Interlocked.Exchange(ref _lastAuthenticated, value.TimestampTicks);
        if (value.Stage is PacketStage.AudioEnqueued or PacketStage.IceEnqueued or PacketStage.RelayEnqueued or PacketStage.SecureMediaEnqueued)
        {
            var observed = Volatile.Read(ref _queueHighWater[(int)value.Stage]);
            while (value.QueueDepth > observed)
            {
                var actual = Interlocked.CompareExchange(ref _queueHighWater[(int)value.Stage], value.QueueDepth, observed);
                if (actual == observed) break; observed = actual;
            }
        }
        if (value.Stage == PacketStage.AudioEnqueued)
        {
            var previous = Volatile.Read(ref _audioHighWater);
            while (value.QueueDepth > previous)
            {
                var actual = Interlocked.CompareExchange(ref _audioHighWater, value.QueueDepth, previous);
                if (actual == previous) break; previous = actual;
            }
        }
        var trace = _trace.Length != 0 && (value.PacketId == 0 || value.PacketId % _options.SampleEvery == 0 ||
            value.DurationTicks >= _options.AnomalyThreshold.TotalSeconds * Clock.Frequency || value.Reason != PacketReason.None);
        var metrics = _metrics.Length != 0;
        if (!trace && !metrics) return;
        // A concurrent drain must never make the media thread wait for consumer code or a large copy.
        if (!Monitor.TryEnter(_bufferGate))
        { if (trace) Interlocked.Increment(ref _traceDropped); if (metrics) Interlocked.Increment(ref _metricDropped); return; }
        try
        {
            if (!Active(Stopwatch.GetTimestamp())) return;
            var safe = _options.IncludePacketIdentifiers ? value : value with { Source = null, Sequence = null, RtpTimestamp = null, SourceEpoch = null };
            if (trace) Append(_trace, _traceRead, ref _traceCount, safe, ref _traceDropped);
            if (metrics) Append(_metrics, _metricRead, ref _metricCount,
                value with { Source = null, Sequence = null, RtpTimestamp = null, SourceEpoch = null }, ref _metricDropped);
        }
        finally { Monitor.Exit(_bufferGate); }
    }
    private static void Append(PacketStageEvent[] ring, int read, ref int count, PacketStageEvent value, ref long dropped)
    {
        if (count == ring.Length) { Interlocked.Increment(ref dropped); return; }
        ring[(read + count++) % ring.Length] = value;
    }
    internal void Lifecycle(PacketStage stage, long started, PacketReason reason = PacketReason.None)
    {
        if (stage == PacketStage.Connection && started == 0) Interlocked.CompareExchange(ref ConnectionStartedAt, Stopwatch.GetTimestamp(), 0);
        var now = Stopwatch.GetTimestamp();
        Record(new(1, 0, 0, DiagnosticPath.HostUdp, PacketDirection.Receive, stage, reason, now, started == 0 ? 0 : now - started,
            0, 0, null, null, null, null));
    }
    internal int QueueHighWater(PacketStage stage) => Volatile.Read(ref _queueHighWater[(int)stage]);
    public PeerDiagnosticSnapshot GetSnapshot()
    {
        Active(Stopwatch.GetTimestamp());
        lock (_bufferGate) return new(PeerEpoch, CaptureEpoch, Clock, (DiagnosticCaptureState)Volatile.Read(ref _state), _traceCount, _metricCount,
            Interlocked.Read(ref _traceDropped), Interlocked.Read(ref _metricDropped), Interlocked.Read(ref _collectorFailures),
            Volatile.Read(ref _audioHighWater), Interlocked.Read(ref _recorded), _lastAuthenticated == 0 ? null : Interlocked.Read(ref _lastAuthenticated));
    }
    /// <summary>Copies at most destination.Length retained trace events, oldest first. No callbacks; safe during a live connection.</summary>
    public int Drain(Span<PacketStageEvent> destination)
    {
        Active(Stopwatch.GetTimestamp());
        lock (_bufferGate) return DrainCore(_trace, ref _traceRead, ref _traceCount, destination);
    }
    private static int DrainCore(PacketStageEvent[] ring, ref int read, ref int count, Span<PacketStageEvent> target)
    {
        var size = Math.Min(count, target.Length);
        for (var i = 0; i < size; i++) { target[i] = ring[read]; ring[read] = default; read = (read + 1) % ring.Length; }
        count -= size; return size;
    }
    /// <summary>Explicit consumer-owned export boundary. Synchronous .NET listeners may block this caller, never the transport.
    /// Throws from collectors are counted and contained. Calls serialize; do not call from a media receive handler.</summary>
    public void Publish()
    {
        if (!_options.Metrics || Volatile.Read(ref _state) >= (int)DiagnosticCaptureState.Cancelled) return;
        lock (_publishGate)
        {
            if (_publishing || Volatile.Read(ref _state) >= (int)DiagnosticCaptureState.Cancelled) return;
            _publishing = true;
            try
            {
            // Instrument creation also invokes user listeners: create here, never on attach or the media thread.
            try
            {
                var instruments = Instruments.Value;
                _activities = instruments.Activities; _events = instruments.Events; _duration = instruments.Duration;
                _queue = instruments.Queue; _collectionDrops = instruments.Drops;
            }
            catch (Exception) { Interlocked.Increment(ref _collectorFailures); return; }
            for (var direction = 0; direction < 2; direction++)
            for (var stage = 0; stage < StageCount; stage++)
            for (var reason = 0; reason < ReasonCount; reason++)
            {
                var index = (direction * StageCount + stage) * ReasonCount + reason;
                var count = Interlocked.Read(ref _counts[index]); var delta = count - _published[index];
                if (delta == 0) continue; _published[index] = count;
                try { _events.Add(delta, Tags((PacketDirection)direction, (PacketStage)stage, (PacketReason)reason)); }
                catch (Exception) { Interlocked.Increment(ref _collectorFailures); }
            }
            var samples = _publishSamples; int size;
            lock (_bufferGate) size = DrainCore(_metrics, ref _metricRead, ref _metricCount, samples);
            for (var i = 0; i < size; i++)
            {
                var sample = samples[i];
                try
                {
                    var tags = Tags(sample.Direction, sample.Stage, sample.Reason);
                    if (sample.DurationTicks > 0) _duration.Record(sample.DurationTicks * 1000.0 / Clock.Frequency, tags);
                    if (sample.Stage is PacketStage.AudioEnqueued or PacketStage.AudioDequeued or PacketStage.IceEnqueued or PacketStage.SecureMediaEnqueued)
                        _queue.Record(sample.QueueDepth, tags);
                    if (sample.PacketId == 0 || sample.DurationTicks >= _options.AnomalyThreshold.TotalSeconds * Clock.Frequency)
                    {
                        var parent = Activity.Current; Activity? activity = null;
                        try
                        {
                            activity = _activities.StartActivity("webrtc." + sample.Stage.ToString().ToLowerInvariant(), ActivityKind.Internal);
                            activity?.SetTag("webrtc.stage", sample.Stage.ToString());
                            activity?.SetTag("webrtc.reason", sample.Reason.ToString());
                            activity?.SetTag("webrtc.observed.duration_ms", sample.DurationTicks * 1000.0 / Clock.Frequency);
                            activity?.SetTag("webrtc.observed.offset_ms", (sample.TimestampTicks - Clock.AnchorTicks) * 1000.0 / Clock.Frequency);
                        }
                        finally
                        {
                            try { (activity ?? (Activity.Current != parent ? Activity.Current : null))?.Dispose(); }
                            finally { Activity.Current = parent; }
                        }
                    }
                }
                catch (Exception) { Interlocked.Increment(ref _collectorFailures); }
            }
            var dropped = Interlocked.Read(ref _metricDropped) + Interlocked.Read(ref _traceDropped);
            try { _collectionDrops.Add(dropped - _publishedDrops); }
            catch (Exception) { Interlocked.Increment(ref _collectorFailures); }
            Array.Clear(samples);
            _publishedDrops = dropped;
            }
            finally { _publishing = false; }
        }
    }
    private static TagList Tags(PacketDirection direction, PacketStage stage, PacketReason reason) => new()
    { { "direction", direction.ToString() }, { "stage", stage.ToString() }, { "reason", reason.ToString() } };
    private void Stop(DiagnosticCaptureState state)
    {
        var previous = Volatile.Read(ref _state);
        while (previous < (int)state)
        { var actual = Interlocked.CompareExchange(ref _state, (int)state, previous); if (actual == previous) break; previous = actual; }
        lock (_bufferGate) { Array.Clear(_trace); Array.Clear(_metrics); _traceCount = _metricCount = 0; }
    }
    public void Dispose()
    {
        Stop(DiagnosticCaptureState.Disposed); _cancellation.Dispose();
        // Do not wait on a collector blocked inside Publish. Its instruments own no transport resources.
        // The shared process-lifetime instruments retain no peer/session and do not require per-capture disposal.
    }
    /// <summary>Call after the consumer's publication loop stops. May wait for a concurrent slow listener; never called by peer disposal.</summary>
    public void DisposePublisher()
    {
        Dispose(); lock (_publishGate) { /* Join only the consumer-owned publication call; shared instruments stay alive. */ }
    }
}

internal readonly record struct DiagnosticDatagram(byte[] Data, PacketDiagnostic Trace);
internal struct PacketDiagnostic
{
    internal readonly PeerDiagnosticSession? Owner;
    private readonly long _id;
    private readonly PacketDirection _direction;
    private readonly DiagnosticPath _path;
    private readonly int _generation;
    private int _bytes;
    private long _previous;
    private uint? _source, _timestamp;
    private ushort? _sequence;
    private long? _sourceEpoch;
    private DiagnosticProtocol _protocol;
    private PacketStage? _previousStage;
    internal void Protocol(DiagnosticProtocol protocol) => _protocol = protocol;
    internal PacketDiagnostic(PeerDiagnosticSession owner, long id, PacketDirection direction, DiagnosticPath path, int generation, int bytes)
    { Owner = owner; _id = id; _direction = direction; _path = path; _generation = generation; _bytes = bytes; }
    internal long LastStageTicks => _previous;
    internal void Size(int bytes) => _bytes = bytes;
    internal void Identify(uint source, ushort sequence, uint timestamp, long? epoch = null)
    { _source = source; _sequence = sequence; _timestamp = timestamp; _sourceEpoch = epoch; }
    internal void Mark(PacketStage stage, PacketReason reason = PacketReason.None, int queueDepth = 0, long? durationTicks = null)
    {
        if (Owner == null) return;
        var now = Stopwatch.GetTimestamp();
        Owner.Record(new(1, _id, _generation, _path, _direction, stage, reason, now, durationTicks ?? (_previous == 0 ? 0 : now - _previous),
            _bytes, queueDepth, _source, _sequence, _timestamp, _sourceEpoch, _protocol, _previousStage)); _previous = now; _previousStage = stage;
    }
}
