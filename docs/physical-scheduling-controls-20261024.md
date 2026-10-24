# Physical scheduling controls

This task owns NativeTimers/scheduling.c, the physical scheduling artifact workflow,
run/check scripts, the authored debugger fixture/workflow checks and the separate
borrowed-media-buffer design. Runtime transport/authentication code is unchanged.

The diagnostic launcher queries PRIO_DARWIN_PROCESS and, on itself only, optionally
sets PRIO_DARWIN_BG or removes that background state using the documented macOS
setpriority/getpriority API. It records observed before/after state, then execs the
same native or managed timer payload. Inherited/background/restored modes rotate
across three fresh process pairs. Native raw/monotonic/wall and managed wake clocks
are compared. Restored means process background state0, not a foreground UI app,
removed QoS clamp, disabled App Nap or absence of host load. Failed/denied policy
changes must remain failed controls, never assumed successful.

No worker, parent, other process, power setting, timer resolution or live audio is
changed. It is a bounded child-owned scheduling experiment, not a product policy.
All local physical execution enters the existing host admission queue. Native
artifacts compile with warnings-as-errors in CI; transfer manifests identify both
the native source and the separately compiled managed source without conflating
them. Raw stderr contains the exact policy marker; any extra diagnostic fails the
control. Raw CSV and partial proof survive failure; no latency waiver is inferred.

Artifact owner/purpose/lifecycle: this task owns
~/.cache/tryagi-webrtc/physical-scheduling-20261024/<native-source> on the selected
physical Mac. Only two precompiled native fixtures, the existing verified managed
test payload and exact small diagnostic scripts are transferred, never repository
sources or installed tools. This consumer runs the rotated timer controls and
retains raw evidence for review. After30-days the diagnostic owner may remove it
only after confirming no active consumer. Existing captures are never overwritten.


## Completed physical process-policy cohort

CI artifact [40099772294](https://github.com/tryAGI/WebRTC/actions/runs/40099772294)
at b056f6d1157b15b9325eae8547371782dd38f719 builds both native fixtures cleanly
with `-Wall -Wextra -Werror`. On one owned physical arm64 Mac, macOS 27.0.1,
.NET 10.0.8/workstation GC/10 reported CPUs, the admitted consumer completes all
nine rotated pairs: 18 fresh processes, 900 native plus 2700 managed wakes.
All child exits are 0; all observed mode transitions match the requested states.
Every pair reports the same managed cohort; no unlike runtimes were pooled.
This is successful diagnostic collection, not successful 20 ms cadence.

The precompiled managed source is separately 06837d4486606583296b356a1250674a12168e57,
DLL SHA256 a685d8aaa9dc6760a02005313f0812c17515785dd731db9addb7c80dd718d5a4,
harness 8ba526f47898406306545457502a2ac4715363f52497854a97d8109a8bd86e21.
Native launcher SHA256 cbc7a88562696c1fa373c8d9a20c4309489573a1d82ddcf5690ffe39f70c282f;
native timer ab411821b793ff42b17cd621c7a39eba3569f426284520ac41765d78555334c3.
The before/after wrapper state is recorded for every actual child before exec;
the native payload itself reports qos_class_self() 17 in all nine processes.
The experiment does not measure the managed thread-pool's effective QoS/clamps.

Each cell below contains the three fresh-process median wake gaps in milliseconds
for a 20 ms request, preserving repetition-level variation:

| Mode / observed background bit | Native nanosleep | Task.Delay | Thread.Sleep |
| --- | --- | --- | --- |
| Inherited0→0 | 92.327 /168.189 /168.008 | 92.706 /167.468 /167.770 | 90.469 /168.327 /168.006 |
| Background0→1 | 118.283 /218.007 /217.906 | 118.544 /118.073 /218.048 | 118.492 /119.196 /218.920 |
| Restored0→0 | 92.696 /165.263 /164.655 | 90.822 /168.257 /168.454 | 91.977 /168.378 /168.970 |

PeriodicTimer medians are 18.596–63.231ms, but ticks can coalesce; those medians
do not establish nominal cadence. Across all modes, median wall/raw ratios are
1.000006–1.000014 for native sleep and 1.000006–1.000033 for managed primitives.
Native monotonic observations are also retained. A large Stopwatch-only scale
error or a crypto-only problem cannot explain these independent timer controls.

Clearing PRIO_DARWIN_PROCESS's background state is insufficient: inherited and
restored children are already 0 and remain slow. Forced backgrounding is slower
in these rotated observations, but host/load/temporal variation is not fully
controlled, and the first repetition differs materially from later ones.
Effective task/application roles, inherited QoS clamps, App Nap and physical host
load remain unresolved. This does not select a production priority/power workaround,
prove a general kernel/runtime cause, or establish audible/provider/device results.
The core library does not own the benchmark caller's 20 ms audio pacing.

Proof SHA256 ff7f5ac8b5e68e7f8c75771e0e2b1ec813dcba414804cabf2fa4ca0fad0101a5;
transfer 50778667df99bff22587efd05e2208f21f0c7b123f04fb6dd3d23298012605ad.
Raw CSV, exact policy stderr and both artifact manifests remain with the owned
physical consumer; a digest-checked full proof is retained locally. Its existing
30-day review lifecycle is unchanged. The earlier failed audio canary is not
reclassified as passing by these timer-only controls.
