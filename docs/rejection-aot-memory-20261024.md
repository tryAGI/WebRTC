# Authentication rejection, NativeAOT budgets and native-memory attribution

## Scope and constraints

Related issues: [19](https://github.com/tryAGI/WebRTC/issues/19),
[15](https://github.com/tryAGI/WebRTC/issues/15), [10](https://github.com/tryAGI/WebRTC/issues/10).
Runtime library code is unchanged by this batch; existing published dev31 remains
the consumer version for the STUN optimization.

.NET10's public AesGcm contract offers Decrypt, which throws on failed tag
validation, without a public TryDecrypt alternative. See the
[pinned framework API](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Cryptography/ref/System.Security.Cryptography.cs)
and [failure contract](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.decrypt?view=net-10.0).
No custom crypto, reflection into framework internals, authentication shortcut,
replay-policy change or runtime dependency is introduced to manufacture zero.

## Rejection workload and acceptance

The shared synthetic fixture distinguishes bad tags, wrong keys and already
accepted replays. All three SRTP profiles cover72/1212-byte RTP and28-byte SRTCP.
Fixture setup, context/key changes and final acceptance happen outside measured
windows. Each of seven samples contains1000 rejection operations after1000 warmup
operations. Caller output remains sentinel-filled; failed authentication must
still allow a later valid packet at the SAME source/index. Wrong-key cases prepare
that packet using the receiver's correct key. Replay cases are preaccepted and
are explicitly separate from authentication work.

The ordinary protocol suite covers buffer/state preservation and exact zero for
AES-CM rejection and all-profile replay guards. Performance rows retain exact
current-thread managed bytes, approximate process allocated bytes, CPU time,
elapsed time and GC deltas. Rejection timing includes sentinel validation and
cannot stand for accepted-media latency or full-network hostile-traffic capacity.

## Managed versus NativeAOT

The native workflow publishes the same framework-only harness for Linux x64,
Linux arm64, macOS arm64 and Windows x64, without suppressing trim/AOT diagnostics.
Ordinary NativeAOT smoke also runs the shared packet/STUN/crypto zero-allocation
and authentication-rejection state contracts, so basic allocation protection is
not limited to manually dispatched performance jobs. Missing-controller deadlines
are additionally exercised with the native harness on every performance platform.
Sequential managed/native processes on each runner retain compiler-mode/source/
runtime/hardware metadata. Each executes147 existing raw zero-byte samples and
real escaping-allocation positive controls. Rejection cohorts use three separate
processes/compiler mode. Timing comparisons are descriptive, without flaky
absolute hosted-runner timing gates.

Rejection budgets use the maximum sample from processes1/2, with independent
process3 as a holdout. A genuinely zero group retains exact0; other proposed
ceilings are maximum training sample x1.25 +64B/op, rounded up to64. The holdout
must pass before registration; later checks never raise limits. Compiler mode is
part of cohort identity, so JIT ceilings cannot be borrowed for NativeAOT.
`calibration_capture=true` validates coverage/zero guards and retains data; it
explicitly does not accept nonzero ceilings. Unknown cohorts fail closed.

Active pipelines additionally cover all40 media/audio/channel/video workload
configurations in three fresh processes/compiler mode. JIT checks retain their
existing stricter reviewed ceilings. NativeAOT calibrates a separate file using
two process medians for training and the third as holdout (max x1.5 +64B/op,
rounded up to64). Unknown compiler cohorts fail; validation never grows limits.

## Extended native-memory controls

The opt-in64-wave cohort preserves four warmup waves and captures disposed
waves2/16/32/63. Three fresh processes/mode compare empty harness, four identities,
crypto-only, audio peers and open-channel peers. Crypto-only creates eight
directional AES-256-GCM contexts and512 authenticated RTP roundtrips/wave, using
fresh synthetic keys; it separates SRTP setup/work from identity/socket/DTLS costs.
The source/backend transport and runtime settings remain untouched.

The redirected-input reader now runs on a worker with a separately cancellable
await, so an open but silent input stream cannot hide the20-second barrier
cancellation behind a synchronous console read. A separate child negative test
leaves stdin OPEN, withholds acknowledgement, and requires failure within30s.
Warmup exercises this worker before any native snapshot; every mode includes the
same controller instrumentation. Profiling can perturb timing and allocator state.

The controller supports24 or64 waves only, with210s/600s outer deadlines and
15s native capture deadlines. Native64-wave child deadline is8minutes. Each
process has to finish with all64 disposed-peer records unrooted. Public artifacts
retain only numeric native aggregates and CSVs; raw native addresses/paths stay
private. Summaries keep three-process min/median/max deltas and tail slopes,
separating RSS, GC committed, malloc resident, allocated and fragment bytes.
Vmmap sizes retain rounding uncertainty. No subtraction is labeled pure native
ownership, and no finite trend is presented as leak freedom.

## Validation status

Initial Python guard/controller checks:53 passed. Exact-source managed/native
measurement, explicit budget registration and extended profiling results are
recorded below after CI completes. Empty initial budget cohorts are deliberately
unreviewed, not permissive fallback limits.

## First exact-source captures and reviewed budgets

Initial paired capture40035032182 (a36b472) and active-expanded capture40035520583
(bbf75fb) both pass on all four platforms. Each compiler/platform runs147 raw-zero
samples and rejects all42 deliberate allocation controls. All27 rejection
workloads have seven samples in three fresh processes. Each active compiler mode
also covers40 configurations in three fresh processes. Existing JIT active limits
remain unchanged and passed every new capture.

GCM failure allocations are not zero: native builds measure240B/rejection in
these cohorts; managed values span192–464B depending on platform/workload/process
phase. The change within some managed runs is observed, not isolated proof of a
tiering cause. NativeAOT does not universally reduce allocation relative to every
managed GCM path. AES-CM and replay guards remain exact zero.

An initial per-workload proposal passed its own process3 holdouts but failed the
earlier independent Windows capture: some later GCM paths moved296→464B while
an earlier GCM path already measured464B in training. That proposal was NOT
registered. The reviewed model pools the identical throwing GCM API's failure-path
training maximum across packets/profiles/modes within each compiler/platform,
while every AES-CM/replay zero stays exactly0. It does not raise established JIT
active budgets or silently grow limits on a failed validation. The registered
GCM ceilings are448B managed Linux,704B managed macOS/Windows,384B NativeAOT;
all independent process3 holdouts and all eight earlier-capture compiler/platform
cohorts pass. NativeAOT active ceilings separately cover40 workloads/platform;
the two training-process medians and independent third-process holdout are kept
in the JSON. A subsequent fresh validation must pass without changing these caps.

## Repeated memory evidence and diagnostic control

All15 fresh extended profiling processes pass in40034866643 at a36b472: five
modes, three processes each,64 measured waves/process after four warmup waves,
with2/16/32/63 native capture barriers and no rooted disposed peer in any record.
Reported live malloc bytes DECREASE in every one of these processes. Resident
malloc grows by similar amounts in empty/crypto/peer modes; this is not evidence
that peer objects alone own the growth. RSS sometimes falls despite growing
malloc resident, so neither RSS nor RSS minus GC committed is a native leak total.

Median wave2→63 malloc resident/live-byte changes, vmmap-rounded:

| Mode | Resident change (MiB) | Live allocated change (KiB) |
|---|---:|---:|
| Empty harness | +4.38 | -131 |
| Crypto only | +4.48 | -119 |
| Identity only | +0.20 | -610 |
| Audio peers | +3.80 | -4170 |
| Open-channel peers | +4.50 | -4028 |

These are approximate binary-unit changes inherited from rounded vmmap sizes.
Peer resident tail slopes between
waves32/63 are small in these samples; empty-harness growth remains appreciable.
A further empty-harness diagnostic disables tiered compilation ONLY in its owned
benchmark child. Its metadata/cohort is explicit and ordinary budget readers
reject it. It is an attribution experiment, not a production/runtime recommendation
or a way to pass allocation budgets. Normal compiler configuration stays default.


## Fresh fixed-budget verification

[40037100667](https://github.com/tryAGI/WebRTC/actions/runs/40037100667) passes on
all four platforms at54603ea0a76e282a0eb8dc4939d6c978791d1708 with calibration
capture DISABLED. All eight managed/native compiler cohorts pass their registered
GCM ceilings,147 existing exact-zero samples/compiler/platform and42 real
allocation controls/compiler/platform. Every compiler/platform has three fresh
active processes passing all40 allocation budgets. JIT active limits are unchanged;
NativeAOT uses its independently reviewed file. All native missing-controller
negative tests pass with graceful exit1 within30s, rather than an unhandled abort.
All managed/native harness builds/publishes report zero compiler warnings/errors.

The ordinary protocol/Pion/Chromium/NativeAOT validation lanes all pass atc1e7386
in40036062021, including the newly shared zero-allocation/state guards in native
smoke. Later54603ea changes diagnostic metadata/budget tooling, not runtime code.
Fifty-seven Python checks pass. Runtime source remains32c3f2bb23f719d236281908c31f8f589bfb674d;
new library adoption is unnecessary for this test/measurement batch.

Timing ratios in the paired JSON are observations from default compiler/runtime
process phases. Allocation warmup is not proof that every managed method has
finished tiering; these data do not establish a universal AOT speedup and do not
serve as absolute latency/throughput gates. The README promotes demonstrated
allocation contracts, not a selected timing ratio.

## Same-source warm-up attribution

Both diagnostic cohorts use54603ea, .NET10.0.13/macOS26.6.2/arm64/3CPUs,
three fresh processes each,64 measured waves and identical disposed-phase
captures. [Default-tiering control40038330151](https://github.com/tryAGI/WebRTC/actions/runs/40038330151)
and [tiering-disabled control40037104294](https://github.com/tryAGI/WebRTC/actions/runs/40037104294)
both pass. The only intentional runtime-control difference is the owned child's
[documented DOTNET_TieredCompilation setting](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation#tiered-compilation).

| Empty harness, three-process medians | Default tiering | Diagnostic tiering off |
|---|---:|---:|
| Malloc resident at wave2 | 3.91MiB | 8.08MiB |
| Malloc resident at wave63 | 8.28MiB | 8.08MiB |
| Per-process resident delta, median | +4.34MiB | 0 |
| Live malloc delta, median | -130KiB | 0 |
| GC committed at either capture | 360448B | 360448B |

Medians of endpoint values and medians of individual deltas need not subtract
exactly. Tiering-off shifts memory earlier; it does not demonstrate a smaller
final footprint. This comparison supports delayed compiler/runtime warm-up and
allocator retention as contributors to empty-harness resident growth. It does
not assign every native allocation owner or prove that all peer/process growth
is harmless. No production compiler or runtime setting was changed; the
diagnostic cohort cannot pass as an ordinary performance-budget cohort.

The repeated controls together retain21 fresh profiling processes and1344
measured lifecycle rows. All peer-bearing disposed records remain unrooted.
Reports and rounded allocator aggregates are retained without native addresses.
Exact ownership of the transport-specific residual and long-duration leak
acceptance remain open in issue10.

## Remaining narrow optimization candidates

Payload-bearing GCM failures still use the framework's throwing decrypt API.
Authentication-only SRTCP and empty-payload packets are a separate GMAC case:
[RFC7714 section9.3](https://www.rfc-editor.org/rfc/rfc7714.html#section-9.3)
uses empty plaintext, and [NIST SP800-38D](https://csrc.nist.gov/pubs/sp/800/38/d/final)
defines the authentication-only specialization. A future, separately measured
change could compute a framework GCM tag for empty ciphertext and compare it in
fixed time. It must retain independent valid/bad-tag/wrong-key/replay vectors,
nonce/index rules and no-output-before-authentication. This batch does not change
crypto runtime code or claim that those unmeasured GMAC cases are optimized.
Mixed accepted/rejected traffic under bounded local network load remains a
separate stress scenario from this synchronous crypto rejection workload.

Final exact-source correctness validation at54603ea also passes all nine lanes
in40037100978, including both independent Pion architectures, Chromium, managed
protocol/network suites and native allocation/state smoke guards. Generic
publication of later test/docs identities is not required to consume unchanged
runtime code and is not treated as a new Advantage runtime delivery.

Local validation limits remain visible: the initial harness build reported0
warnings/0 errors (receipt20261023-fd8accc83162431db488b304fa592e84,33.074s wall).
The subsequent scoped rebuild never entered compilation because host admission
timed out after301.595s (receipt20261023-afd027cd3328475792d10ea9d0ae2358,
native6a5aa8a29b0444d892438d86874cad20), an SLO breach. No admission/compiler
bypass or warning suppression was used. The exact-source hosted validation and
fresh managed/native matrices above verify the final harness independently.
