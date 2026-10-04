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

## Confirmed SCTP setup defect

RFC 8841 [section 9.3](https://www.rfc-editor.org/rfc/rfc8841.html#section-9.3)
requires both WebRTC SCTP endpoints to initiate an association. SDP `setup`
controls DTLS and does not select the SCTP initiator. Through package 0.2.2,
`PeerConnection` incorrectly used a passive SCTP responder when acting as the
DTLS server. Another passive SCTP endpoint therefore stalls after successful
ICE/DTLS until the SCTP deadline. The fixed implementation always initiates SCTP;
DCEP stream parity still follows DTLS, as required by RFC 8832.

The immutable published 0.2.2 regression project reproduces this exact failure:
DTLS-server/passive remote, successful ICE/DTLS and `MediaReady`, then SCTP
`SctpInit` timeout with zero retransmissions. A DTLS-client positive control
connects against the same passive remote. Fixed-source tests require all four
DTLS-role/remote-SCTP-role combinations to establish and exchange data both ways,
including simultaneous INIT and DTLS-based stream parity. The low-level
`SctpAssociation` still exposes the passive role for explicit transport use/tests.

This confirmed standards defect is a candidate explanation for issue #3, not a
proven diagnosis of its original provider attempts. The original trace did not
separate DTLS and SCTP. Fresh Advantage acceptance must identify the failing phase
and verify the fixed package before issue #3 can close.

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

The published baseline is independently runnable:

```sh
dotnet run --project src/tests/WebRTC.PublishedRegression -c Release
```

It pins the exact first-party `tryAGI.WebRTC` 0.2.2 package instead of referencing
the source library; the dependency is confined to a non-packable test executable.
Three-platform and native arm64 CI must observe the expected old timeout and
successful positive control. Failure in another protocol or successful old
passive/passive establishment does not count as a reproduction.

The standard three-platform CI, dedicated native Linux arm64 lane and executed
whole-root NativeAOT smoke include
isolated DTLS/SCTP silence, missing DCEP ACK, total-deadline classification,
repeated independent peers in both DTLS roles and history wrap/disposal checks.
The arm64 lane builds/runs the same managed protocol/network suite and executes
its linux-arm64 native binary on an aarch64 host. A separate native arm64 lane runs
the complete pinned independent Pion suite, asserting host and container
architecture. These lanes do not emulate the deployed ECS network or replace a
provider canary. The native runner label follows
[GitHub runner documentation](https://docs.github.com/en/actions/reference/runners/github-hosted-runners);
linker prerequisites follow [Microsoft NativeAOT guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

These authored fixtures use only local UDP and do not contain private consumer
code or provider traces.
