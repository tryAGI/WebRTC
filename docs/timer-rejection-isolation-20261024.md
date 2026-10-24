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
