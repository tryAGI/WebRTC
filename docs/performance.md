# Performance measurements

The opt-in **Performance baseline** workflow measures authenticated SRTP RTP
protect/unprotect roundtrips on native Linux x64/arm64, macOS arm64 and Windows.
It has no provider traffic or new dependencies. Local equivalent:

```
dotnet run --project src/tests/WebRTC.Performance -c Release
```

The harness warms 1,000 packets and measures seven samples of 5,000 packets for
60-byte audio-like and 1,200-byte video-like payloads across all three profiles.
Each operation checks authenticated plaintext equality and uses increasing sequence
numbers. Preallocated caller buffers exclude packet-buffer construction. Managed
bytes come from the synchronous measuring thread; timing includes validation and
comparison. Key/context creation and initial source-dictionary growth are excluded.

Results are microbenchmarks, not full transport throughput, latency percentiles,
retained/native memory, codec cost, energy, production performance or leak evidence.
Do not infer a hard timing regression from a single shared CI runner. Compare
repeated samples with matching architecture/runtime, and preserve raw CSV artifacts.
No noisy timing threshold blocks normal CI. The project is compiled by normal CI;
measurement runs only on explicit dispatch.

## AES-CM optimization under validation

Issue #12 identified 512–704 managed bytes per 60-byte protect/unprotect pair and
9.6–13.2 KiB per 1,200-byte pair across the four .NET 10.0.13 baseline platforms.
AES-GCM measured zero for these preallocated-buffer crypto-only operations.

AES-CM now owns one reusable framework ECB block transform per directional RTP/RTCP
cipher. This is the block primitive for the existing CTR construction, not ECB
packet encryption: salt, SSRC, packet index, block counter and HMAC semantics are
unchanged. The enclosing SrtpContext gate serializes all operations and disposal.
Two fixed 16-byte scratch buffers never escape and are cleared after every packet
operation and on disposal. Constructor failures also dispose the transform.
A warmed allocation regression checks authenticated RTP/SRTCP roundtrips across all
profiles; existing independent vectors, rollover, hostile inputs and lifetime tests
remain required. Independent Pion and full platform validation are required before
calling this an accepted optimization. No profile preference is changed.

## Expanded suites

Use `-- --suite packets`, `media`, `video`, or `channels` with the same project.
The default remains `crypto`. The opt-in workflow retains separate CSVs and CPU
hardware metadata on all four native platforms.

- `packets`: warmed RTP+MID parsing, truncated RTP rejection, RTCP framing and
  STUN fingerprint/attribute validation. Normal protocol CI enforces zero managed
  allocation and includes an escaping-allocation positive measuring control.
- `media`: authenticated DTLS/SRTP+ICE loopback delivery across all profiles,
  60/1,000-byte payloads, tracing disabled/enabled and default/cancellable caller
  tokens. One packet is outstanding; received bytes and zero queue drops/rejections
  are checked. Five samples of 512 packets follow 128 warmups. Process-wide managed
  allocation includes socket/queue/harness/maintenance work; p50/p95/p99 cover this
  controlled single-hop workload, not production network latency or congestion.
- `video`: bounded two-fragment H264/VP8 reassembly with preallocated input and
  checked reconstructed bytes. Input is already trusted; this excludes SRTP and
  decoding. Owned output allocations are expected and measured, not labeled zero.
- `channels`: bounded reliable SCTP data-channel delivery, including fragmentation
  for 4,096-byte messages. Process-wide allocation includes the owned peers and
  harness after connection/channel setup and warmup.

Media completion reports the managed heap after a forced collection and process
working set/private bytes where available. These whole-process observations include
runtime/native caches and do not isolate native allocations or prove leak freedom.
Unavailable process-private memory is labeled unavailable, never zero.

Initial local .NET 10.0.9/macOS arm64 media baseline (before transport changes):
AEAD AES-256-GCM median process-wide allocation was ~1,017 B for a 60-byte payload
and ~3,849 B for 1,000 bytes, default caller token and tracing disabled. Cancellable
caller cases measured ~1,033/~3,865 B. These include harness costs; use repeated
same-runtime before/after cohorts for attribution. All 65 protocol cases, including
new parser allocation contracts, passed locally. Suite compilation: zero warnings
and errors. No claim about an allocation-free complete transport follows from this.

## Bounded media-send optimization

Source c3ac505 reuses a private framework ArrayPool for encrypted output: maximum
retained array length 2 KiB and maximum 16 arrays per bucket. Larger configured
packets can rent uncached arrays; the existing configured datagram bound still
applies. Each peer holds at most one rental under the send gate. The exact protected
length is sent; capacity padding is never transmitted. Host socket sends consume
memory before completion; relay framing copies into its own packet before its await.
The rental returns cleared in a finally block after completion/failure/cancellation.
Public caller snapshots and received owned arrays keep their original lifetimes.
Default caller cancellation uses the peer lifetime token directly; cancellable
callers retain a linked source. This does not eliminate all transport allocations.

Matched .NET 10.0.13 medians, AES-256-GCM, tracing off, default caller token:

| Platform | 60-byte payload before → after (B/packet) | 1,000-byte payload before → after (B/packet) |
| --- | ---: | ---: |
| Linux x64 | 1,017.31 → 841.31 | 3,842.47 → 2,729.03 |
| Linux arm64 | 1,017.31 → 836.44 | 3,849.48 → 2,729.41 |
| macOS arm64 | 1,020.59 → 841.12 | 3,850.52 → 2,732.77 |
| Windows x64 | 1,008.97 → 841.31 | 3,837.98 → 2,730.00 |

All 96 profile/size/trace/token combinations had lower median allocation across
these cohorts. Cancellable cases preserve their required linked-source cost;
AES-256-GCM reductions were ~10–11% for 60 bytes and ~27% for 1,000 bytes.
These are process-wide async pipeline observations, not isolated per-site counters.
Pool and default-token changes were measured together, so do not attribute the
whole reduction to either one alone. No timing improvement is claimed from noisy
shared runners. Source/hardware/raw samples:
[before 2562112](https://github.com/tryAGI/WebRTC/actions/runs/39967229913),
[after c3ac505](https://github.com/tryAGI/WebRTC/actions/runs/39968888140).

Local lifetime tests passed for every profile: variable-length RTP and SRTCP,
already-cancelled/malformed/oversized rejection, retained received arrays,
slow-reader eviction and disposal with a send burst. These do not deterministically
force a socket send to remain pending; independent relay/interop validation and
longer retained-memory stress remain separate evidence.

## Connection lifecycle and retention suites

`--suite cold` creates eight fresh connections for each profile with SCTP disabled
and enabled, checks the first bidirectional encoded-audio delivery and separately
times setup, delivery and disposal. It retains construction/SDP and per-peer
ICE/DTLS/SCTP phase durations/retransmissions as numeric comment records. Those
spans overlap; do not sum both peers' durations into an outage. These are new
connections within one process, not independent cold-process launches. Early
samples include first-use JIT/native caches.

The same suite measures fresh SRTP directional key/context generations, including
RNG, first authenticated packet, caller buffers and disposal. Every generation uses
new random key/salt and clears external key material. This is **not** negotiated
in-session DTLS rekey, which the library does not implement.

`--suite audio` measures complete PeerConnection bidirectional encoded-audio
routing/ownership for all profiles, diagnostics off/on and 60/1,000-byte payloads.
Five samples follow warmup. Each sample has 64 batches of eight packets in each
direction; latency percentiles are **batch** durations, not individual wire latency.
Process-wide allocation includes both peers, socket/queue work and harness tasks.
Synthetic payload equality/timestamps and zero unexpected drops/rejections are
required; this does not decode or play Opus.

`--suite soak` has a four-minute hard workload deadline. For every profile and
tracing off/on, four lifecycle cycles each create three concurrent audio-peer pairs,
exchange 256 packets per direction over eight epochs, reject already-cancelled
sends, then exercise bounded slow-reader eviction and retained received buffers.
The normal cohorts total 294,912 packet deliveries. Intentional eviction drops are
reported separately; unexpected normal drops/authentication/routing errors fail.
After each cohort, all six disposed peer objects must be collectible after at most
three forced collections with bounded scheduling gaps. Peer creation failure also
disposes any already-created sibling.

Memory snapshots include live/disposed managed heap after collection, process RSS,
private memory/handles where supported, and disposed-peer reachability. Unavailable
private/handle observations are labeled unavailable. OS counters have different
meanings across platforms. RSS can retain JIT, crypto, allocator and runtime caches;
a short bounded cohort and collectible peers do not establish native leak freedom,
long-duration stability or device energy. Forced-GC soak timings are not throughput
benchmarks and no noisy absolute RSS threshold is a normal-CI acceptance gate.

The opt-in workflow retains cold/audio/soak CSVs even on failure on the four native
platforms. Runtime/library identity and CI harness source are recorded; local dirty
harnesses explicitly report unverified working-tree provenance. Initial exploratory
macOS .NET 10.0.9 execution passed the cold suite and all 24 soak cohorts with zero
reachable disposed peers. Managed post-disposal heap was approximately 0.4–0.6 MB,
while RSS grew across early cohorts and later approached a plateau: this warrants
longer native-resource measurement, not a leak-free claim. Final exact-source CI
results remain separate from those local observations.

Four-platform exact-source run
[39996100914](https://github.com/tryAGI/WebRTC/actions/runs/39996100914) passed all
expanded suites at 1972708. All 576 disposed peer instances across four platforms
were collectible; 1,179,648 normal soak packet deliveries completed. Process RSS
still grew across the short cohorts, despite low post-collection managed heap;
this is not native leak acceptance. Follow-up strengthens slow-reader retention
checks with different subsequent payload bytes so aliasing cannot hide behind
identical content. Repeat final-source results remain a separate gate.

Cold phase data attributes a notable macOS cost to the two-peer construction span,
which includes identity/certificate, socket and queue work. It does not isolate
certificate cost or explain a remote/provider recovery. Follow-up
[#16](https://github.com/tryAGI/WebRTC/issues/16) preserves matched phase/hardware
metadata and requires native profiling before changes to identity lifetime.

## Idle maintenance and native-construction attribution

`--suite idle` records three two-second idle samples after authenticated connection
and a short warmup, all profiles, tracing off/on and one/three peer pairs. A no-peer
harness control is included. Process-wide managed allocation, CPU, GC counts,
consent/retransmission counts and completed thread-pool work items are observed.
Thread-pool completions are a scheduling proxy, not kernel wakeup counts. Packet
integrity/queue checks remain active before/after the idle period.

`--suite identity` separates key/public-key export, request construction, certificate
creation, verification and certificate/key disposal for fresh identities. It compares
framework CreateSelfSigned (private-key-associated certificate), framework Create
with an explicit fresh-key signature generator (public-only certificate), and the
actual runtime identity path. Public key equality and a synthetic signature prove
certificate/key correspondence; private keys are never exported or shared.
Samples include initial-use effects; compare later samples on matched machines.

`--suite native` compares eight waves of 16 creations/disposals per method after
32 warmups, with empty controls. CI uses fresh processes for each method and an
explicit DOTNET_TieredCompilation=0 control to remove background re-tiering; this
is a benchmark configuration, not production runtime acceptance. Local default
interleaving remains available, with TRYAGI_NATIVE_MODE selecting a single method. It records post-collection managed heap, GC committed/fragmented
bytes, process RSS/private memory/handles where available and CPU. The four-minute
workload deadline is checked between native operations; a native framework call
itself has no cancellation API. The workflow job deadline remains the outer bound.
Interleaving reduces order bias but does not isolate each method's retained native
bytes in a shared process. Managed committed bytes are not resident pages; never
subtract them from RSS and label the remainder pure native allocation. A native
allocator profile or process-isolated repetition is separate evidence.

Initial local compilation was queued behind another owner's exclusive host request;
no compiler bypass or source copy was used. The committed four-platform baseline
is the validation/measurement gate before runtime timer or identity changes.
CSV numeric formatting is invariant-culture for portable parsing.

### SCTP idle and isolated longer retention

`--suite idle-sctp` measures AES-256-GCM audio pairs with data channels off,
associated without DCEP, and a warmed bidirectional open channel. Each has tracing
off/on and one/three pairs, three two-second samples after a one-second settling
period. SCTP retransmission deltas and outstanding/send/receive occupancy are
retained; nonzero final occupancy fails the fixture. Open channels remain owned
by the peers until disposal. Ordinary `idle` continues to cover all SRTP profiles
without data channels. Completed threadpool work is not a kernel wakeup count.
CI retains three separate-process repetitions per native platform before setting
statistical budgets. The baseline must precede runtime timer changes.

`--suite retention` uses an allowlisted `TRYAGI_RETENTION_MODE` (empty_control,
identity_only, peer_audio, peer_open) and trace 0/1. Fresh-process cohorts use four
warmup waves and 24 measured waves, two fresh peer pairs or four identities per
wave, two seconds live idle and 512 bidirectional synthetic audio deliveries per
peer wave. Open-channel waves also prove bidirectional binary routing before
idling. Every peer is disposed and checked through weak references after bounded
full collections. Records retain heap, GC committed/fragmented bytes, RSS,
available private bytes/handles and elapsed time. The process deadline is three
minutes, CI's outer job bound 25 minutes. No source copy or native dependency is
introduced. These finite cohorts improve attribution; they do not prove prolonged
leak freedom or identify precise native allocated bytes by subtracting GC memory
from RSS. Controls keep JIT/tiering defaults to match actual peer workloads.

### Reviewed allocation budgets

The budget checker consumes raw total bytes for 42 crypto and 105 packet samples.
The packet cases cover parsing/framing, caller-buffer STUN writing (plain, SHA-1
and SHA-256), and valid/wrong-key authentication using small stack and warmed
pooled input buffers containing a 4-KiB attribute. Protocol contracts also check warmed replay,
truncation and short-destination SRTP/SRTCP rejection across all three profiles,
plus bad-tag AES-CM rejection. AES-GCM bad-tag exceptions are outside this zero
contract. In every guarded rejection, caller output remains unchanged and later
valid authentication succeeds. Cold pool misses or concurrent pool exhaustion
can allocate; the large-message contract explicitly describes warmed reuse.
For the zero subset, its zero-byte contracts never use rounded bytes/operation. The benchmark-only
`TRYAGI_BENCHMARK_ALLOCATION_CONTROL=1` injects one escaping 256-byte array per
crypto sample, and the positive-control verifier requires that the same exact-zero
validator reject it. This option introduces allocation; it cannot disable a gate.

Idle budget calibration uses process repetitions 1 and 2 for training and keeps
repetition 3 as an independent holdout. Per workload, the proposed ceiling is the
maximum training median times 1.5, plus 8 KiB/s, rounded up to 8 KiB/s. This is an
explicit noise allowance for process-wide counters and periodic consent, not a
zero-allocation claim. Holdout failure blocks calibration rather than increasing
the ceiling. Validation never rewrites limits. Runtime version, OS family,
architecture, processor count, GC mode and dynamic-code mode identify a reviewed
cohort; missing cohorts fail for review. Source/harness metadata, exact workload
coverage, drained queues and retransmission-free idle intervals are mandatory.

The process-wide gate is initially scoped to the new repeated idle cohorts.
Packet/crypto exact-zero guards remain in ordinary protocol CI. Audio/media/data
throughput and native RSS remain measured without invented timing or leak gates;
repeat-process noise characterization is required before adding their budgets.

The checked-in `.NET 10.0.13` idle budgets were calibrated from source
`a20b2ad5b11100e606f917ba7e586944977d8cc2`, run 40013111120, with all four
independent process-3 holdouts passing. Those same limits reject the real old
polling baseline run 40012542666 on every platform. Reproduce with
`python3 scripts/check-performance-budgets.py RESULTS_DIRECTORY`. The performance
workflow now executes exact-zero, positive-control and idle budget checks and
retains JSON verdicts. Its `long_retention` input defaults true; false skips only
the longer fresh-process retention step when checking gates after a fully
measured runtime change. Ordinary protocol/network and independent peers are
unchanged. These initial process budgets cover idle workloads; repeat-process
throughput allocation budgets and NativeAOT performance remain future scope.

### Active workload allocation ceilings

Active gates additionally cover40 existing workloads per native cohort:
24 protected media combinations (three profiles, 60/1000-byte payloads,
tracing off/on, caller cancellation default/explicit),12 bidirectional encoded
audio combinations,60/4096-byte data-channel messages and H264/VP8 trusted-input
two-packet reassembly (1005/1000-byte owned output frames). Five samples form
each median. Media/audio/channel counters are process-wide; video is current
thread. Owned outputs and fixture/queue costs remain included and nonzero.

Two independent process captures, runs40013111120/sourcea20b2ad and
40015452037/source6bcd687, train active ceilings. The runtime library tree is
identical (`efd11dd08a4651e4ca5ab82b0db03f68d8ef381b`); active workload classes,
shared fixture and build inputs were compared unchanged. The second harness
only adds a crypto-only positive-control branch, outside active dispatch paths.
Each ceiling is max of the two training medians x1.5 +64 bytes/operation,
rounded up to64. A fresh native-matrix holdout is required before acceptance;
validation never adjusts ceilings. No elapsed-time or native-leak gate is inferred.

`check-active-performance-budgets.py` rejects incomplete/duplicate workloads,
source/runtime mixtures and unreviewed cohorts, and retains JSON verdicts.
The workflow's `active_only=true` is a focused gate verification mode after
unchanged runtime idle/retention acceptance; defaultfalse retains all suites.
Normal crypto/parser controls and all four active suites remain mandatory in
this mode. Protocol and independent-peer workflows remain unchanged.

### Phase-matched native profiling

The opt-in retention profiling controller captures waves 2 and 16 after all peers
have been disposed, bounded full collections have completed and every disposed
peer weak reference is dead. A startup acknowledgement warms console handling
before any peer wave. The child waits for an exact wave acknowledgement while
`vmmap -summary` inspects that owned process; the controller never sends suspension
or resume signals. Each capture has a 15-second timeout, each child barrier a
20-second cancellation deadline, and the controller a 210-second outer deadline.
This instrumented run perturbs timing and is separate from ordinary retention CSVs.

On macOS, run through host admission when required by the local machine policy:

```sh
python3 scripts/profile-retention-macos.py artifacts/bin/WebRTC.Performance/release/WebRTC.Performance.dll /tmp/webrtc-profile-new --mode peer_open --trace 1
```

The output directory must be new. It retains the complete CSV, a bounded phase
summary and two raw native summaries. Treat raw files as private: they can contain
process identifiers, addresses and local paths. Repeat with `empty_control`,
`identity_only` and `peer_audio` in fresh processes to attribute differences.
Matched disposed-peer occupancy removes the earlier live/disposed phase ambiguity;
finite samples still do not prove native leak freedom or precise native ownership.

STUN verification uses the same one-shot framework HMAC APIs as the writer,
replacing the previous per-call incremental HMAC object. It authenticates a copy
with the RFC-adjusted header, never alters caller storage, and keeps fixed-time
tag comparison and strict attribute-envelope checks. Stack input is capped at
2 KiB; larger valid STUN datagrams use a cleared framework pool buffer returned
on every path. Tests cover either side of that boundary and the maximum framed
message size, plus existing independent SHA-1/SHA-256 vectors and tampering.
