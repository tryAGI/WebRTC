# Native allocation owners and quiescence

The previous default/off controls left residual resident growth in peer_open
(issue10). The new manually dispatched `native-owners.yml` adds three fresh
empty-control/peer-open pairs on one macOS runner, interleaving their order.
All children use tiering-off and MallocStackLogging. This instrumented cohort
is deliberately separate from ordinary allocation and memory budgets.

The owned child blocks at post-disposal waves2,16,32,63, then after10 and20
additional seconds of quiet waiting. Capture time is excluded from those wait
labels and included in elapsed wall time. Each barrier requires zero weak peer
roots. CSV snapshots add thread-pool threads, pending work and completed work.
The controller captures vmmap and malloc_history only for its own child, with
time/output bounds and cancellation cleanup. It never suspends another process.

Only numeric summaries are public. Raw native stacks, process descriptions,
addresses, SDK paths and child stderr stay in job-owned temporary directories
and expire with the hosted job. There is no new persistent cache. CI artifacts
retain numeric CSV/JSON for30days; their consumer is
`summarize-native-owners.py` and issue10 analysis.

Allocation stacks are grouped by the nearest recognized native nonallocator
frame. Managed callers can be unsymbolicated: a runtime frame is descriptive,
not proof that the runtime caused an allocation. Requested VM reservations and
malloc requests are separate and can overlap; neither equals resident memory.
RSS, GC committed memory, malloc resident bytes and live requested bytes retain
separate units. A quiet-period reduction can support a settling hypothesis but
cannot establish leak freedom. Native profiling also perturbs allocator behavior.

The first peer stack capture exceeded the initial16MiB tool-output limit
(run40060754767); it was rejected rather than summarized partially. The
controller now allows at most64MiB per capture with the original30second
deadline unchanged. Empty-process capture already completed all six barriers
in that run, but one process is not a three-pair attribution result.
