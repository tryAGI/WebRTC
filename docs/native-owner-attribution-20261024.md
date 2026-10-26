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

## Completed repeated capture

[Run40061135149](https://github.com/tryAGI/WebRTC/actions/runs/40061135149)
completed all six fresh processes and36 native captures at source
`7140805fd201b6d6f76e3dc0eaff2d4dd38768f9`: .NET10.0.13, macOSarm64,
three CPUs, workstation GC, JIT, tiering-off. The full log contained zero
compiler/tool warning diagnostics. Downloaded numeric CSV/JSON independently
recomputed the exact same summary, using the final source/hardware/phase guards.
All768 measured disposed peer weak references were clear; the three peer
processes also had48 warm-up peers. Pending thread-pool work was zero at every
captured phase. These are instrumented cohorts, not ordinary performance budgets.

Median per-process changes:

| Measurement | Empty wave2→63 | Peer wave2→63 | Peer final quiet change |
|---|---:|---:|---:|
| Process RSS |−32KiB|+944KiB|−1824KiB|
| Malloc resident |0|about+0.2MiB|0|
| Live malloc allocated |0|+45KiB|−182KiB|
| Managed committed |0|0|0|
| Thread-pool threads |0|+12|−29|

The three peer thread trajectories at wave2/final/quiet30 were19→23→2,
19→31→2 and19→36→2. Final quiet RSS was below wave2 in **every** peer process
by912/880/848KiB. Peer managed committed memory stayed6,127,616bytes throughout.
The empty controls stayed at two workers, with approximately flat memory.
Medians of per-process changes are not necessarily additive across columns;
malloc sizes inherit vmmap rounding rather than byte-exact resident precision.

Nearest-native-frame requested-size groups narrow the attribution:

- Runtime-group malloc requests grew by median64,672bytes from wave2→63 and
  fell137,984bytes during the final quiet period.
- Foundation-group malloc requests grew23,552bytes then fell44,960bytes.
- Crypto-SDK malloc requests grew only560bytes then fell1328bytes; its VM request
  size stayed flat. This is not evidence of ongoing crypto request accumulation.
- **Unknown-kind requests with a nearest .NET runtime frame** grew by
  median19,464,192bytes, then fell47,038,464bytes as workers retired. They remain
  unknown-kind requests, not relabeled malloc, VM residency or proven stacks.
  The reported call-count change was generally two requests per changed worker;
  this supports a worker-associated native reservation hypothesis without naming
  the exact unpreserved allocator frame.

The reversible RSS/request changes and worker retirement support a substantial
runtime-worker contribution to the previous short-lived residual. The remaining
roughly0.2MiB malloc-resident increase despite lower live requested bytes is
consistent with allocator retention/fragmentation, but is not fully attributed.
No uninstrumented counterfactual, symbol-complete AOT trace, general leak-freedom
proof or production/device acceptance is claimed. Issue10 stays open for those
remaining boundaries; issue20 still blocks clean macOS AOT symbol quality.
