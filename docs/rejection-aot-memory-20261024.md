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
