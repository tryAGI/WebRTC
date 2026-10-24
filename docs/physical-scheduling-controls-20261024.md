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
retains raw evidence for review. After30days the diagnostic owner may remove it
only after confirming no active consumer. Existing captures are never overwritten.
