# Opt-in realtime diagnostics (contract version 1)

`tryAGI.WebRTC` uses the .NET framework's `Meter` and `ActivitySource`. Core has
no exporter, logging/DI requirement or additional runtime package. Attach a live
capture with `PeerConnection.AttachDiagnostics`; lower-level ICE owners can use
`IceUdpTransport.AttachDiagnostics`. Recording defaults to off until attachment;
detailed packet traces default to off even after attachment.

## Keep collection outside media processing

The transport records numeric value types into bounded buffers and fixed counters.
It does **not** invoke .NET listeners, start Activities, format strings, perform I/O
or await a consumer from an instrumented path. Call `Publish()` from your own
publication loop. It copies/drains metrics under a short buffer lock, releases that
lock, then calls framework instruments. Slow listeners stall that publication
caller only; throwing listeners increment `CollectorFailures`. Recursive publication
is ignored. No background tasks or exporters are created by the library.

`MeterListener.InstrumentPublished`, metric callbacks and Activity listeners can all
run synchronously. Never call `Publish()` from a socket/media handler, under your
media locks or inside your playback callback. A blocked listener cannot be forcibly
terminated by this library. Stop your publication loop before `DisposePublisher()`;
that method may wait for an in-flight collector. Peer disposal never waits for it.
`Dispose()`/`DetachDiagnostics()` immediately stop recording and clear trace/metric
rings. The one bounded instrument set has process lifetime and retains no capture/peer.
A throwing instrument-creation listener disables publication for that process
(the failure is cached rather than creating unbounded duplicate instruments);
trace draining and transport continue. Measurement/Activity callback failures are
contained per publication and future calls can recover after the listener is removed.

This follows the framework instrumentation APIs described in
[Microsoft's metrics guide](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation)
and [ActivitySource guide](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs).

## Consumer-owned collection example

Run this collection loop separately from your audio receive/decode loop. The arrays
are reused; a sink should copy only the records it needs to retain. Connect an
exporter of your choice to instrumentation name `tryAGI.WebRTC`, version `1.0`.

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using tryAGI.WebRTC;

using var capture = peer.AttachDiagnostics(new PeerDiagnosticsOptions
{
    Metrics = true,
    PacketTrace = true,
    IncludePacketIdentifiers = false,
    SampleEvery = 10,
    CaptureDuration = TimeSpan.FromSeconds(30),
    MaximumBufferBytes = 512 * 1024,
    EventCapacity = 2048,
});
var scratch = new PacketStageEvent[capture.TraceCapacity];
using var meter = new MeterListener();
meter.InstrumentPublished = (instrument, listener) =>
{
    if (instrument.Meter.Name == PeerDiagnosticSession.InstrumentationName)
        listener.EnableMeasurementEvents(instrument);
};
meter.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
{
    // Consumer-owned sink; bounded labels only. This runs inside capture.Publish().
});
meter.SetMeasurementEventCallback<double>((instrument, value, tags, state) => { });
meter.SetMeasurementEventCallback<int>((instrument, value, tags, state) => { });
meter.Start();
using var activities = new ActivityListener
{
    ShouldListenTo = source => source.Name == PeerDiagnosticSession.InstrumentationName,
    Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
    ActivityStopped = activity => { /* Consumer-owned lifecycle/anomaly sink. */ },
};
ActivitySource.AddActivityListener(activities);
try
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    while (await timer.WaitForNextTickAsync(cancellationToken))
    {
        capture.Publish();
        int count = capture.Drain(scratch);
        // Send scratch.AsMemory(0, count) to your own sink, outside media processing.
        // Snapshot retains explicit overflow counts even if the trace is incomplete.
        if (capture.GetSnapshot().State != DiagnosticCaptureState.Active) break;
    }
}
finally
{
    peer.DetachDiagnostics();
    capture.DisposePublisher(); // This example's publication loop has ended.
}
```

## Clocks, identity and coverage

Every event has `Version=1`, a capture-local packet ID, direction, protocol, path,
path generation and `PreviousStage`. Correlate with the snapshot's opaque
`PeerEpoch`, `CaptureEpoch` and `DiagnosticClock`. Reattachment retains the peer epoch but starts a
new capture epoch and capture-local packet-ID sequence: **never join IDs across separate capture
instances**. A new peer gets a new epoch. This transport has one ICE credential
generation and one nominated path; generation 0 means pre-nomination, 1 means
nominated. ICE restart/renegotiation is not supported; create another peer.

Timestamps/durations use `Stopwatch.GetTimestamp()` ticks and the stated frequency,
within the process's monotonic domain. The UTC anchor is approximate and useful for
log alignment only. Wall clocks, unrelated processes, RTP clocks, NTP sender reports
and kernel clocks must not be subtracted as if synchronized. RTP timestamps retain
the caller's media clock; sending does not rewrite them to wall time.

| Boundary | Implemented evidence | Unknown / limitation |
| --- | --- | --- |
| Host UDP / TURN UDP receive | Managed `ReceiveFromAsync` completion, handler duration, rearm and next-arm records | Not wire/kernel arrival; OS wake-up vs network/provider pacing inseparable |
| TURN TCP receive | Each managed NetworkStream read and completed TURN frame | Stream reads are not one UDP packet; frame assembly can span reads |
| TURN TLS receive | Each decrypted SslStream read and completed TURN frame | Raw socket completion, TLS internal buffering/decryption and kernel arrival are unavailable |
| Kernel timestamps / socket overflow | Explicit `false` support flags | No fabricated timestamp or zero overflow count |
| Receive pipeline | Demux, TURN/ICE queue enqueue/dequeue, SRTP authentication/decryption start/end, secure-media queue, audio queue, consumer delivery | Consumer scheduling after iterator delivery, decode/playback and physical audio are outside core |
| Send pipeline | Caller submission, audio/video semaphore wait, secure-send semaphore wait, protection gate/work, TURN stream write wait, socket/stream write start/completion | Core has no audio pacing or send queue; caller-owned pacing and peer receipt are unavailable |
| Route / RTCP | Selected candidate types/path/consent age, independent ICE and SR/RR RTT ages/staleness, per-source reception state and remote RR about transmitted streams | No synchronized one-way latency, decoder PLC/FEC evidence, RTX/NACK transport or all-NAT proof |

`Bytes` describes the currently observed representation (caller payload, protected
media or TURN framing); it never contains payload. Queue depth is a bounded
concurrent observation, not an atomic history of all readers. Queue high-water is bounded by the configured queue capacity. `GetQueueEvidence()`
returns live depth, capture-observed high-water and oldest instrumented-item age for
audio, ICE, secure-media and attached TURN queues. Age/high-water are unavailable
when the item/capture was not instrumented; relay high-water covers the capture
across bounded relay paths, rather than an unbounded per-endpoint map. Queue dequeue duration measures the
elapsed time since its matching enqueue, including scheduler/consumer wait. The
separate `ConsumerDelivery` stage is the iterator yield boundary, not application
callback start. Receive handler duration includes awaited response handling; rearm
and the following arm expose managed scheduling. Pre-receive network delay remains
outside those durations.

For Opus send, `CatchUpBurst` records advancing RTP time delivered in less than half
its media-clock interval. This is an observation, not a built-in pacer. Video and
advanced RTCP do not claim this Opus-specific classification. Internal RTCP report
budget/raw-request scheduling and SCTP packet-stage tracing are not exported in v1;
SCTP lifecycle is exported.

## Bounded capture and drop semantics

Capture duration is 10 ms–5 minutes; event capacity 8–8192. Numeric buffers obey
`MaximumBufferBytes` (32 KiB–4 MiB), including fixed counter arrays and the reusable
publication scratch ring. `BufferBudgetBytes` reports the conservative reservation;
CLR object headers, instrument/listener objects and the consumer's own scratch/sink
are outside that numeric-buffer budget. Expiry freezes the bounded trace for a final
drain and stops recording; disposal/cancellation clear it. There is no expiry task.

Normal detailed events use `SampleEvery`. Threshold-duration events, drops and
lifecycle events are retained independently; their earlier stages may be absent.
Ring-full or buffer-lock contention drops **diagnostic records**, never media.
`TraceEventsDropped` and `MetricEventsDropped` are separate from existing audio,
ICE, TURN and secure-media drop counters. Counters remain exact for recorded stage
calls while histogram samples can be lost; every lost sample is counted. An
incomplete trace cannot establish that an unrecorded stage did not happen.

`Dropped` includes `PreviousStage`, identifying the boundary, and a bounded reason:
queue overflow, invalid route/consent, framing, source/negotiation, authentication,
replay-window/too-old decision, cancellation, shutdown or send failure.
A replay-window rejection occurs before authentication and does **not** prove an
authenticated duplicate. Reordered audio is delivered under the existing transport
policy; it is annotated using the existing RFC 3550 tracker. Duplicate tracking
covers the tracker's sequence comparison, not an additional unbounded packet history.

## RTP/RTCP evidence and privacy

`GetRtpEvidence()` reads the existing `PeerRtcp`/`RtpReceptionTracker` without
advancing RTCP reporting intervals. Reception snapshots include an opaque stream epoch, clock rate,
probation readiness, reset epoch, fraction lost (0–255 / 256), cumulative loss
(which may be negative), extended sequence, jitter in RTP clock ticks, sample ticks
and age. Confirmed sequence restart increments the source reset epoch; ordinary
sequence/timestamp rollover does not. Distinct SSRCs have independent bounded
state. Remote reports are explicitly **about our transmitted stream**, with local
clock rate and observation age. Missing reports/RTT remain unavailable; SR/RR RTT
expires after one minute, separately from ICE consent/check RTT.

Raw SSRC/sequence/RTP timestamp trace fields require `IncludePacketIdentifiers`.
Receive identifiers appear only after authentication. `GetRtpEvidence(true)` and
`GetRouteEvidence(true)` are separate explicit identity/endpoint opt-ins. Existing
legacy snapshots retain their existing fields. Standard diagnostics have no audio,
SDP, passwords, credentials, keys, authentication headers or transcripts.

Metric tags are only finite `direction`, `stage`, `reason`; no peer/session ID,
SSRC, sequence, endpoint, timestamp or other unbounded label is emitted. Instruments:

- `webrtc.stage.events`: counter, events, recorded stage calls.
- `webrtc.stage.duration`: histogram, milliseconds between the stated observable boundaries.
- `webrtc.queue.depth`: histogram, packets at sampled queue boundaries.
- `webrtc.diagnostics.dropped`: counter, diagnostic records lost (not media loss).

Lifecycle and threshold anomaly Activities describe **publication-time spans**.
`webrtc.observed.duration_ms` and `webrtc.observed.offset_ms` explicitly carry the
original observation. Activity duration is not handshake/packet latency. No packet
Activity is created by the media thread or for normal packets.

## Validation and measurements

Run the deterministic local cases with:

```sh
dotnet run --project src/tests/WebRTC.IntegrationTests -c Release -- --case-filter Diagnostics
```

The suite measures 100 actual 60-byte encoded Opus packets at 20 ms pacing for off,
aggregate-only and bounded-trace modes, after warm-up. It reports process allocation
(including both local peers, publication and reused consumer drain), process CPU,
wall time and send-to-iterator-delivery p50/p99. Framework listener callbacks are
no-ops; exporter costs and physical playback are excluded. Measurements are noisy,
not a latency SLA or proof that instrumentation is free. Release evidence records
actual CI measurements and source SHA. The whole-library NativeAOT lane also
executes diagnostics lifecycle, route, collector and local fault cases.

No paid provider, public relay, credentials or physical device are used in these
checks. Consumer upgrade/A/B acceptance still needs the same provider, audio fixture
and playback endpoint on both implementations.
