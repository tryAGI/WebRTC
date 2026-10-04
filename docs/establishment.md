# Establishment failures and Advantage acceptance

Current source also provides [retained build identity and bounded ready-state trickle](build-identity-and-trickle.md).

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

## Provider admission blocked on deployed 0.2.5, 2026-10-04 22:50 UTC

A fresh bounded series first verified the actual serving image, health 200 and
`tryagi.webrtc/0.2.5/lib/net10.0/tryAGI.WebRTC.dll` in the production publish log.
All 10 tryAGI attempts and both SIPSorcery controls then received HTTP 429 at
`call_create`, before ICE/DTLS. All five tryAGI phase statuses were `NotStarted`;
all seven rejection counters were zero. Packet-boundary and rejection projections
were present in the actual consumer. Every result reported zero microphone samples
and zero tools; active voice settings were unchanged.

This is **inconclusive provider acceptance**, not a transport failure-rate result.
Do not repeat the unchanged series while admission is blocked. The next useful
probe needs provider admission restored, with protocol establishment and physical
playback measured separately. Keep issue #3 open and do not change the default
transport on the basis of local/independent tests alone.

[Sanitized results and proof hashes](https://github.com/tryAGI/WebRTC/issues/3#issuecomment-5985350232)
are retained in the issue; the complete sanitized trace remains private. No raw
provider signaling, credentials or media are published. The current driver ran all
12 requests despite admission failure; future drivers should fail fast on 429 and
other non-protocol admission errors instead of presenting them as DTLS failures.

## Fresh provider evidence, 2026-10-04

Two subsequent independent Advantage probes with the adapter declared pinned to
0.2.2 reproduced the timeout with retained phase evidence. ICE succeeded in
288.2 and 274.5 ms, followed by a DTLS-client timeout waiting for
`DtlsServerHello` in 15013.2 and 15014.5 ms. Each recorded three DTLS retries,
zero rejected DTLS records, and SCTP/DCEP `NotStarted`. Caller cancellation was
false in both cases. The first recorded clean disposal in 2.1 ms. An intervening
successful probe completed all phases, but the sequential sample is incomplete
and is not a reliability comparison.

The SCTP initiation fix below does **not** explain these particular failures:
SCTP never started. Zero rejected records cannot distinguish no response at the
socket, a drop before DTLS, or an incomplete/reordered handshake assembly.
Capture packet boundaries on the deployed adapter before choosing a protocol fix.
The ABI `AssemblyVersion` is 0.0.0.0 throughout these experimental 0.x packages;
it cannot attest an exact NuGet version. The deployment/package identity must be
verified separately; a service task definition is not proof of the running task.

Opt-in packet capture now includes each outgoing DTLS handshake record and retry
at socket send boundaries. Incoming DTLS classification starts at demultiplexing
using the RFC 7983 content-type range. This classification is not authentication.
Managed socket-send completion does not prove remote receipt; a zero captured
count is usable only after checking capture expiry, truncation and dropped events.
Payloads, endpoints and handshake secrets remain excluded. These diagnostics do
not alter handshake deadlines, record limits, fingerprint binding or replay checks.

## Confirmed SCTP setup defect

RFC 8841 [section 9.3](https://www.rfc-editor.org/rfc/rfc8841.html#section-9.3)
requires both WebRTC SCTP endpoints to initiate an association. SDP `setup`
controls DTLS and does not select the SCTP initiator. Through package 0.2.2,
`PeerConnection` incorrectly used a passive SCTP responder when acting as the
DTLS server. Another passive SCTP endpoint therefore stalls after successful
ICE/DTLS until the SCTP deadline. Package **0.2.3** always initiates SCTP;
DCEP stream parity still follows DTLS, as required by RFC 8832.

The immutable published 0.2.2 regression project reproduces this exact failure:
DTLS-server/passive remote, successful ICE/DTLS and `MediaReady`, then SCTP
`SctpInit` timeout with zero retransmissions. A DTLS-client positive control
connects against the same passive remote. Fixed-source tests require all four
DTLS-role/remote-SCTP-role combinations to establish and exchange data both ways,
including simultaneous INIT and DTLS-based stream parity. An additional local
proxy drops one or both initial encrypted SCTP INIT records after ICE/DTLS are
established; tests require recovery and bidirectional DCEP data. The two-sided
loss case must exercise at least one handshake retry: the first retry can establish
the association before the other initiator needs to retransmit. These cases execute under
NativeAOT as well. The low-level
`SctpAssociation` still exposes the passive role for explicit transport use/tests.

This confirmed standards defect is not the cause of the fresh DTLS-client
timeouts above. Its relationship to the original provider attempts is unproven. The original trace did not
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

## Retained ICE data-admission rejection counters

The next diagnostic build adds `ice.GetDatagramRejectionCounts()` and
`peer.GetEstablishmentEvidence().IceRejectedDatagrams`. Each returns an immutable
seven-category snapshot, independent of capture attachment, expiry, sampling or
trace truncation, retained after timeout and disposal. Categories report the first
failed predicate in the original admission order: `TransportStopped`,
`LocalPathUnavailable`, `NoNominatedPair`, `PathMismatch`, `SourceMismatch`,
`Oversized`, `ConsentExpired`. A packet with a wrong source and an excessive size
is counted only as `SourceMismatch`. These are cumulative totals, not correlated
packet or flight identifiers. The existing `InvalidRouteOrConsent` trace category
is unchanged so existing bounded exporters remain compatible.

The scope is non-STUN datagrams that reach the ICE data filter. It excludes
TURN framing/permission rejection, STUN parsing, queue overflow, DTLS framing and
authentication. A zero counter alone never proves no remote transmission or no
managed receive. Snapshots contain only finite reasons and counts, never addresses
or payloads. Consumers must explicitly add this field to their safe DTO projection;
upgrading the package alone does not update a consumer's serialization contract.

Actual local UDP tests cover no nomination, unavailable host path under relay-only
policy, source mismatch (also oversized), oversize on the valid nominated source,
and arrival on an attached relay when the host path was selected. They prove exact
counts despite overflowing an eight-event capture, immutable prior snapshots,
valid selected traffic still passing, and retention after disposal. A peer-level
silent-DTLS timeout retains an unknown-source ICE rejection and zero DTLS rejected
records after cleanup. Shutdown/expired-consent counters preserve existing guards;
these additional tests do not claim a separately forced arrival in those race windows.
No provider root cause is inferred from these local negative controls.

## Required Advantage adapter change

1. Upgrade the explicitly selected experimental adapter from 0.1.0-dev.37 to the
   **0.2.3**, including retained evidence and the SCTP initiation fix. Keep the
   existing production default.
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

## BUNDLE route regression

Fresh separately invoked Advantage probes against the serving 0.2.4 adapter on
2026-10-04 produced two successful establishments and one DTLS ServerHello timeout.
The failed capture had no trace loss or omission: four 114-byte DTLS socket sends
completed, and six 687-byte incoming DTLS datagrams were rejected at ICE with
`InvalidRouteOrConsent`, before DTLS parsing. ICE nomination succeeded and consent
responses continued; SCTP/DCEP never started. This establishes a receive/admission
boundary, not the exact rejected predicate or the provider's SDP topology.

Source inspection found a separate concrete negotiation defect: candidates from
all accepted media sections were merged into one ICE checklist. [RFC 9143 sections
7.3.1 and 10](https://www.rfc-editor.org/rfc/rfc9143.html#section-10) select transport
properties from the negotiated BUNDLE-tagged section. The implementation now uses
only that section's signaled candidate list. For a remote initial offer, it follows
the tag selected by the local answer, including rejection of the suggested tag.
Explicit post-negotiation trickle candidates still apply to the bundled transport.
This change leaves source, path, consent, size, fingerprint and replay guards intact.
The existing requirement for consistent explicit ICE/DTLS properties across accepted
sections remains; this is not complete general JSEP or tag-only attribute support.

An authored local two-route fixture reproduces the pre-DTLS timeout against the
exact published 0.2.4 DLL, independently of the source library. Both UDP routes
answer authenticated ICE; bundled server DTLS originates only from the tagged
route. A higher-priority candidate on the non-tagged data section causes the old
library to nominate the wrong route and drop ServerHello before parsing. The fixed
source establishes bidirectional DCEP, retains wrong-source rejection and receives
permitted authenticated Opus after that negative packet. The normal pinned 0.2.2
baseline also executes the expected old failure; managed and executed NativeAOT
lanes execute the fixed fixture and tag-selection cases.

The provider's raw SDP and source tuples are intentionally absent from these
fixtures. The matching local failure boundary does not prove that this BUNDLE
case caused the real provider incident. Repeat acceptance against the actually
deployed fixed package, retaining precise ICE counters, remains required for #3.
