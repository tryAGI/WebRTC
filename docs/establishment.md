# Establishment failures and Advantage acceptance

[Issue #3](https://github.com/tryAGI/WebRTC/issues/3) records an actual Advantage
canary on Linux arm64 / .NET 10 with **0.1.0-dev.37**: four independent provider
connections, two successes and two consumer `media_connect` timeouts. All four
nominated ICE with no ICE retries. Two subsequent legacy transport controls
succeeded. This is useful production evidence, but that small sequential sample
is not a reliability estimate.

The approximately 15-second failures do not identify a protocol: both the DTLS
and SCTP handshake defaults are 15 seconds, and the old consumer measurement
combined `MediaReady`, `ConnectAsync` and channel opening. There is no fresh
provider trace proving that a particular protocol defect is fixed by a newer
release. Keep the existing production transport default until repeat acceptance.

## Retained establishment evidence (version 1, package 0.2.2)

`peer.GetEstablishmentEvidence()` works before starting, during establishment,
after timeout and after `DisposeAsync`. It is independent of opt-in packet capture.
It retains a fixed ring of **64** numeric handshake/lifecycle events and exact
phase/attempt/retry totals. Old events are overwritten with an explicit counter;
snapshot arrays are immutable copies. The journal is written only at handshake
and lifecycle boundaries, with no media packet instrumentation or exporter.

Five phases separate overall connection, ICE nomination, DTLS/SRTP, SCTP, and
local DCEP OPEN attempts. Each has a status, expected protocol step, finite failure
category, start offset, elapsed time and retry count. Elapsed freezes when that
phase ends. Pending/attempt/success/failure totals and event operation numbers
identify concurrent local OPEN attempts; DCEP status describes the latest
completion when no attempts remain, not the lifetime health of every channel.
Remote OPENs and payload delivery are not local OPEN attempts.

`TerminalState`/`TerminalFailure` preserve the pre-cleanup peer outcome, even
when public `State` subsequently becomes `Closed`. A total establishment deadline
now produces `TimeoutException`; explicit caller cancellation stays cancellation.
Child linked-token cancellation is attributed to the owning deadline when known.
A DCEP caller token cannot reveal whether the application canceled manually or
its own timer expired: record that distinction in the consumer. Disposal is not a
new protocol failure. The negotiated SRTP profile remains available in legacy
DTLS diagnostics after shutdown. Authentication and replay policies are unchanged.

All timestamps use the supplied `ClockAnchorTicks` and `ClockFrequency` from
`Stopwatch`; they are process-local, not kernel receive or server wall clocks.
`Lifetime` spans peer construction through its terminal protocol outcome and
excludes cleanup. Measure disposal separately. Once an outstanding channel-open
task completes, its final evidence also remains after disposal. Snapshot access
is safe during operation, but aggregate counters from different transports can
advance between individual reads.

No SDP, IP addresses, call IDs, fingerprints, certificates, cookies, keys,
channel labels/protocol strings, exception messages or media are included.
`PeerEpoch` is random per instance, not a provider identity. Packet trace still
requires explicit attachment and final draining **before** detach/disposal;
that larger buffer's lifecycle is unchanged.

## Required Advantage adapter change

1. Upgrade the explicitly selected experimental adapter from 0.1.0-dev.37 to the
   **0.2.2**. Keep the existing production default.
2. Measure call creation/admission, `MediaReady`, complete `ConnectAsync`, local
   channel OPEN and cleanup independently. `MediaReady` proves authenticated
   media readiness, not SCTP or DCEP readiness.
3. On each result, retain `GetEstablishmentEvidence()` after cleanup and inspect
   the failing phase/expected step. For packet detail, attach a bounded capture
   before `ConnectAsync` and drain it before disposal. Do not log the legacy ICE
   diagnostic record wholesale: it includes endpoints.

Consumer outline (signaling, audio drain and logging remain consumer-owned):

```csharp
var connectAt = Stopwatch.GetTimestamp();
var connecting = peer.ConnectAsync(connectionToken);
try
{
    await peer.MediaReady.WaitAsync(connectionToken);
    var mediaReadyMs = Stopwatch.GetElapsedTime(connectAt).TotalMilliseconds;
    // Start the existing consumer's media drain here, preserving queue/pacing rules.
    await connecting;
    var connectionMs = Stopwatch.GetElapsedTime(connectAt).TotalMilliseconds;
    var openingAt = Stopwatch.GetTimestamp();
    await peer.OpenDataChannelAsync(parameters, channelOpeningToken);
    var openingMs = Stopwatch.GetElapsedTime(openingAt).TotalMilliseconds;
    // Retain these separate values, rather than one media_connect number.
}
finally
{
    // Drain optional packet capture before this point.
    var disposalAt = Stopwatch.GetTimestamp();
    await peer.DisposeAsync();
    var disposalMs = Stopwatch.GetElapsedTime(disposalAt).TotalMilliseconds;
    // Observe connecting's terminal result even if MediaReady threw first.
    try { await connecting; } catch (Exception) { /* Original error remains primary. */ }
    var establishment = peer.GetEstablishmentEvidence();
    // Export only this bounded DTO using the consumer's approved serializer/logging.
}
```

## Separately invoked real acceptance

Repeat at least ten independent provider peers with fresh local sockets and clean
teardown, on the same deployed adapter/package and environment. Keep microphone,
tools and paid audio coverage out of routine smoke; use only the specifically
invoked provider establishment canary. Stop on unexpected side effects. Retain
all attempts, including failures, and exact package/source/deployment proof.
Compare an explicitly invoked legacy control under the same conditions.

Record finite phase outcomes and timings above, expected DTLS/SCTP steps,
ICE/DTLS/SCTP retransmissions/rejections and DCEP attempts. Do not publish raw
provider signaling. A successful local/Pion/browser connection or ten successes
alone does not establish provider reliability or Watch playback improvement.
A reproduced provider failure must be traced to a bounded protocol regression
before claiming its root cause repaired; missing fresh trace remains explicit.

## Local regression lane

```sh
dotnet run --project src/tests/WebRTC.IntegrationTests -c Release -- --case-filter Establishment
```

The standard three-platform CI and executed whole-root NativeAOT smoke include
isolated DTLS/SCTP silence, missing DCEP ACK, total-deadline classification,
repeated independent peers in both DTLS roles and history wrap/disposal checks.
These authored fixtures use only local UDP and do not contain private consumer
code or provider traces.
