# Independent clocks and managed rejection attribution

The manual timer-isolation workflow compares native nanosleep and .NET timers on
one physical runner per OS cohort (macOS15,macOS26,Linux). Three fresh process
pairs interleave order. Every wake records wall and raw monotonic clocks; native
code also records CLOCK_MONOTONIC. Native/managed source labels must match.
Coverage/nonfinite checks fail incomplete data without treating slow wake-ups as
successful latency acceptance. Wall-clock adjustments remain explicit values.
Periodic ticks can coalesce and bunch; no50Hz proof follows from a low median.

A separate framework-only decrypt benchmark measures valid AES128GCM opens,
bad-tag failures and a synthetic managed AuthenticationTagMismatchException.
It uses one synthetic60byte sealed message, preallocated buffers,1000warmups,
seven measured windows of at least500ms, counted operations, thread allocations
and process CPU. Valid output must match; failed framework opens must leave only
zeros. The synthetic throw is an attribution control, not a mathematically
subtractable component of the different crypto exception path. Three fresh
uninstrumented processes run per OS. No socket, replay, pacing wait or provider
runs here, and no native/whole-transport allocation claim is implied.

Pinned Microsoft dotnet-trace10.0.750501 captures separate synthetic children.
Only managed sampling and JIT/loader providers are enabled, not exception payload
events. A16MiB buffer,20s collection bound,60s invocation timeout and128MiB final
trace bound apply. The tool's report is sampled managed thread time, not kernel
CPU measurement; native costs and safepoint bias remain unknown. Trace timing and
allocations must not replace the uninstrumented cohort. Complete diagnostics,
workload exit status and raw trace remain visible.

Owner/purpose/lifecycle: WebRTC diagnostic workflows own artifacts/clocks,
artifacts/rejection-traces and artifacts/diagnostic-tools/trace-10.0.750501.
The stable local tool path is used only by collect-rejection-traces.py; it is
ignored, never installed globally or used by Advantage. Job tools/build outputs
are destroyed with the job; checked CSV/proof and synthetic traces are retained
30days. No runtime NuGet/native dependency, SDK modification, live voice session,
provider/device probe, infrastructure change or paid AWS/S3 operation is added.


A separate native-timer-qos control changes only the synthetic child's main
thread to QOS_CLASS_USER_INITIATED for100 wakes and confirms the effective class.
Default/user-initiated processes interleave3 times on the same Mac. Each exits;
no global policy, power setting, production process or managed thread is changed.
This tests a scheduling/coalescing hypothesis, not authorization to raise runtime
priorities or adopt the control as a fix. All raw/wall/monotonic clocks are retained.


## Exact-source results

[Run40090661802](https://github.com/tryAGI/WebRTC/actions/runs/40090661802)
atd41c4cf0428fa4d4a6f8c114e12b9b549bad6b98 completed all four jobs, with
zero compiler warnings/errors. Three pairs per OS give900 native and2700 managed
wake observations overall. Both wall and raw clocks show the large hosted Mac
wake gaps; native CLOCK_MONOTONIC agrees too. This excludes a large Stopwatch-only
scaling error as the explanation in this cohort.

| Cohort | Native sleep median across process medians, ms | .NET Delay median across process medians, ms | Wall/raw ratio |
| --- | ---: | ---: | ---: |
| macOS15 |89.063|91.763|~1.0000|
| macOS26 |118.003|115.829|~1.0005|
| Linux |20.072|20.114|~1.0000|

These are same-runner native/managed controls per cohort, not matched hardware
across OSes, a provider result or device audio acceptance. MacOS26's~0.05% clock
difference cannot explain fivefold wake delays. PeriodicTimer still coalesces;
its lower medians do not prove uninterrupted cadence.

The uninstrumented60byte AES128GCM/managed throw measurements (21 samples/mode/OS)
retain exact counters and checked success/rejection output. Median ns/op and
thread bytes/op are summarized below; CPU/process and raw sample data are retained.

| Cohort | Valid us / B | Bad-tag us / B | Synthetic throw us / B |
| --- | ---: | ---: | ---: |
| macOS15 |3.606 /0|9.150 /192|3.709 /296|
| macOS26 |3.223 /0|7.046 /192|3.136 /296|
| Linux |0.550 /0|1.907 /192|1.479 /296|

The independently instrumented Mac job passed all three child workloads. Its
thread-time report assigns37.65% exclusive sampled weight to EH.DispatchEx
in bad-tag mode, with42.8% unresolved symbols and15.91% in the Apple decrypt
interop boundary. Synthetic throw assigns81.79% to EH.DispatchEx; valid mode
has92.55% unresolved symbols. These report percentages include startup/rundown
and safepoint/native-symbol limitations; they are not exclusive kernel CPU or a
causal subtraction of framework/native costs. They support exception handling as
a real cost while the timer-only controls independently explain a major pacing
confounder. No supported payload-bearing-GCM nonthrowing decrypt API was substituted.

## Native QoS control

[Run40091205202](https://github.com/tryAGI/WebRTC/actions/runs/40091205202)
at85d14a7156a3880a85e1239c5bcbc3d9e0658e51 passed both Mac lanes with
600 native wake observations each. Effective native thread class changed17→25.
On macOS15 the three default/initiated process medians were73.891/57.247,
70.857/58.291 and62.968/53.163ms. On macOS26 they were47.966/72.157,
50.698/59.115 and53.320/57.636ms. Both wall/monotonic clocks retain the delays.
This process-owned class change did not normalize20ms cadence and is not a
runtime fix. Differing runner state/hardware prevents treating the lower default
medians relative to the earlier cohort as an improvement caused by any code.
The remaining process activity/coalescing/host scheduling cause is unproven.


The follow-on native activity control uses the same Objective-C/ARC binary in
all arms (default, native thread QoS, process-owned latency-critical/user-initiated
activity), with rotated3-process order per Mac cohort. NSProcessInfo's activity
token stays owned until endActivity after100 wakes, then the child exits. This
finite diagnostic may inhibit idle sleep/coalescing for its own process; it does
not set a global policy or add a power/priority assertion to the library. It
isolates an activity-policy hypothesis, not authorization to adopt it in product
code or an energy/thermal conclusion. Metadata retains actual thread class and
activity state with every raw/wall/monotonic capture.


The process activity follow-up
[run40092650294](https://github.com/tryAGI/WebRTC/actions/runs/40092650294)
atcffdc53bfbaee6a7d7cb6f076181f517e383c493 passed both Mac lanes,1800 wakes
across18 fresh processes. Default and initiated controls explicitly record
activity=off; the latency-critical/user-initiated activity arm explicitly records
activity=latency-critical. Raw captures were independently rechecked with the
stronger22e3275 activity-identity guard (rejecting active/ambiguous baselines).
On macOS15, default/initiated/activity medians were100.950/78.758/114.161,
106.563/69.513/45.247 and92.169/80.126/106.170ms. On macOS26 they were
62.377/56.152/59.244,41.646/65.550/61.100 and41.787/50.133/49.760ms.
Neither scoped QoS nor this activity assertion restored20ms cadence consistently.
The data does not prove App Nap is the cause, and does not justify adding either
assertion to runtime code. The remaining hosted scheduling/coalescing environment
requires a physical matched-runtime control or host-level evidence.


Terminal batch checks:113 Python guard tests pass. The planner and performance
projects compile with0warnings/errors locally; planner execution and corrected
LLVM positive/negative control pass through host admission. Busy-host admission
SLO breaches and the earlier corrected harness and dynamic-serialization/AOT errors remain recorded
in compiler-eh-candidate-20261024.md. No Advantage dependency/deployment or runtime
transport policy was changed in this diagnostic/compiler-candidate batch.
