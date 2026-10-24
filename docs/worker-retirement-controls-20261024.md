# Worker retirement and instrumentation controls

The previous instrumented capture associated reversible RSS/native-request growth
with worker retirement, but did not distinguish profiler overhead or causality.
The manual worker-retention workflow adds a bounded2×2 counterfactual on one host:
three fresh processes each with default/minimum24 workers and stack logging off/on.
Every child runs24 measured peer-open waves plus four warm-up waves, then10/30second
quiet waits. Controls are interleaved and repetition2 reverses their order.

The minimum24 intervention belongs only to an owned diagnostic child. It warms24
workers using a cancelable finite rendezvous before peer measurements and keeps
that minimum for the child lifetime. It changes neither library defaults nor any
Advantage/runtime service. The recorded host must have fewer than24 CPUs and
observed minima must match their controls. All snapshots require zero disposed
peer roots and pending thread-pool work. More workers may exist during active work;
quiet-period counts reveal whether the intervention actually retained them.

Source/runtime/hardware identities must match across all12 processes; intervention
labels remain separate. Stack instrumentation and minimum-worker metadata cannot
borrow ordinary allocation/memory budget cohorts. Per-phase RSS, GC committed,
malloc residency/live bytes/fragmentation and worker counts remain separate.
Traced cohorts also retain sanitized native-owner groups; unknown request kinds
remain unknown. No raw addresses or symbols are published. No causal or general
leak-freedom claim follows until the repeated numeric results support it.

Outputs are job-owned worker-controls directories consumed by
summarize-worker-retention.py; only numeric CSV/JSON is retained30days. Raw vmmap,
malloc_history and stderr expire with the job. Each child/controller and capture
retains a finite deadline/output bound, with at most12 children in the45minute job.
No persistent cache, runtime dependency or provider/device operation is added.
