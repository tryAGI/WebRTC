# Apple crypto bridge and hostile-load attribution

The manual apple-crypto workflow separates the unchanged pinned .NET Apple crypto
Swift bridge from managed exceptions, sockets and scheduling. It builds one
Swift6 strict-concurrency optimized driver object, then links that same object
against the MIT upstream fixture built-Onone and-O, without LTO. It verifies the
fixture hash, uses three fresh processes per optimization, alternates order in
process2, and retains84 checked samples per process: AES128/256,0/60/1200byte
payloads, valid and bad-tag open, seven5000-operation samples after1000warmup.
The only imported source remains the already-attributed NativeSymbols fixture.
The authored driver borrows local array pointers synchronously, with no escaping
pointers or concurrent access. Bad tags must produce the bridge's authentication
failure classification and leave the output sentinel unchanged; valid opens must
match every plaintext byte. These are native timing observations, not allocation
counters, general security acceptance or a production replacement. The binaries
contain identical driver bytes but different bridge code by design. Neither SDK
archive nor WebRTC runtime is modified.

A separate macOS26 managed process runs the existing synthetic loopback workload.
The harness marks measured phase start after handshake/warmup, without packet or
session data. The profiler attaches only to its own child for3seconds at each
AES128GCM burst64/256 phase and preserves the workload exit code/partial CSV/full
stderr independently of profiler success. Instrumentation can change scheduling;
stack sample counts are inclusive and must not be summed into exclusive CPU time.
A missing phase or workload timeout fails, rather than manufacturing coverage.
The exact-source CSV/runtime metadata remains authoritative for this cohort.

All generated objects, binaries and stack captures are job-owned artifacts used
only by these diagnostics; jobs destroy compilation outputs after completion.
CSV/proof and synthetic stacks are retained30days for issues22/24. This introduces
no runtime dependency, persistent cache, SDK edit, provider call or device action.


The first stack run40075886152 obtained both requested captures, but its observer
240s bound terminated the otherwise-running workload after35/36 rows. That is an
observer deadline, not a transport/integrity exception. The observer now allows
420s for the whole12-scenario campaign (each scenario still has its unchanged30s
workload deadline), records observer failures explicitly, and retains a finite
job bound. No load, delivery, coverage or per-scenario limit was weakened.


## Captured findings

Atde3b21d38f42f0a12d400c7115cca3678a66606e, native bridge job in
[run40075886152](https://github.com/tryAGI/WebRTC/actions/runs/40075886152)
passed504 samples in6 fresh processes with Swift6.1.2/Xcode16.4/SDK15.5.
AES128/60byte valid median was3.715us with both builds; bad-tag median was
3.678us-Onone versus3.570us-O. AES256/60byte valid3.678→3.510us,
bad-tag4.090→4.049us. Some other cells regressed with-O. These interleaved
hosted-runner observations do not establish a universal optimization speedup.
Local Swift6.4/SDK27 controls also passed all504 samples, with roughly1us
opens; they are a different toolchain/hardware cohort, not a before/after trend.
This is insufficient evidence to replace framework crypto or blame-Onone for
multi-second producer stalls. No zero-native-allocation measurement is claimed.

The instrumented managed job captured both requested3s phases. Its35 completed
rows lost1 valid packet, then the observer killed it at240s; no workload exception
was observed before termination. AES128 baseline100-packet sample median was
6.590s with only111ms process CPU. Burst64/256 sample medians were6.125/6.148s
with489/1154ms process CPU. Timer/scheduling delay therefore already exists in
that baseline; received-only latency and accepted counts cannot establish50Hz
continuity. Stacks include the Apple bridge, Swift error-type conformance and
managed IL_Throw/unwind paths. Many JIT frames lack symbols; inclusive samples
are not sufficient to rank exclusive CPU cost or attribute the entire stall.

At4e886877b7aca5e13167590b5fc74b4841acd3c1,
[run40076148182](https://github.com/tryAGI/WebRTC/actions/runs/40076148182),
all three framework-only timer processes completed300 wake samples each on
.NET10.0.13/macOS26.6.2/arm64 before transport profiling:

| Control | Per-process median wake gap, ms | Max wake gap, ms | Gaps over40ms, total/300 |
| --- | --- | --- | --- |
| Task.Delay(20ms) |119.277 /121.186 /116.161|223.617|252|
| PeriodicTimer(20ms) |20.040 /14.319 /14.994|171.909|112|
| Thread.Sleep(20ms) |125.633 /106.032 /112.433|180.137|239|

The300 samples per process were independently validated with the timer checker
introduced later atdc3aec0. No socket, crypto, peer or WebRTC receive processing
runs in this control. It isolates a significant hosted macOS timer/scheduling
confounder, but not yet its OS/runtime/clock/QoS cause. PeriodicTimer can coalesce
and bunch ticks, so its lower median is not uninterrupted50Hz proof. The subsequent
profiled workload in this run exited1 before either target capture, retaining its
failure. Local .NET10.0.9/macOS27.0.1 control completed300 wakes with medians
20.970/20.028/21.615ms and maxima22.885/22.090/24.322ms; different cohorts cannot
prove an OS regression or production repair. No busy-wait/high-priority workaround
or timer-policy change has been introduced into the runtime.


The corrected-observer follow-up
[run40076213882](https://github.com/tryAGI/WebRTC/actions/runs/40076213882)
at824543e49d5087044f1f866b7243d505441a30bf exited1 during AES-CM/burst0,
before any hostile traffic or requested capture. Two complete100-packet samples
received100/100 each, but took11.357s/9.882s with max180.637/180.559ms send gaps.
The unchanged30s scenario deadline then expired during pacing; the fixture retained
`auth_load_failure=deadline`, full TaskCanceledException and nonzero status.
Observer_failure=null and no external termination occurred. This executes the
previously-unverified graceful workload failure path: incomplete CSV is rejected.
It demonstrates a significant baseline environment/timer confounder, not a repair
of the distinct prior hostile-load packet loss or physical audio acceptance.
No failed capture was substituted with the green native bridge job.
