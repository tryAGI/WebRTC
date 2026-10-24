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

## Completed factorial capture

[Run40064372353](https://github.com/tryAGI/WebRTC/actions/runs/40064372353)
completed all12 fresh processes and60 native phase captures at source
c5c7c7cc392dd93521898c0246b4714637384e1c, .NET10.0.13/macOSarm64/3CPU,
with zero compiler/tool warnings. Source/cohort/phase guards independently
recomputed the downloaded results. GC committed stayed6,127,616bytes in every
peer snapshot, with no retained disposed-peer weak roots or pending pool work.

Observed quiet30 medians:

| Control | RSS | Malloc resident | Live malloc | Physical workers |
|---|---:|---:|---:|---:|
| Plain, default minimum |76448KiB|about12.7MiB|1836KiB|2|
| Plain, minimum24 |76960KiB|about13.0MiB|1858KiB|2|
| Traced, default minimum |85136KiB|about16.6MiB|2011KiB|2|
| Traced, minimum24 |85248KiB|about16.7MiB|2017KiB|2|

The minimum24 intervention **did not retain24 physical workers**. It raised
active counts substantially (35–50 versus6–22 in the observed default snapshots),
then every process returned to two. This agrees with the documented distinction
between an on-demand creation minimum and actual idle worker population. The
hypothesized permanent floor was not established and must not be used to infer
one worker's retained RSS from final group differences.

Quiet RSS reductions were median704KiB/plain-default,2048KiB/plain-min24,
848KiB/traced-default and2704KiB/traced-min24. These changes with deliberately
higher worker populations strengthen the reversible worker-associated component;
unknown native request kinds still do not become proven stack allocations.

Stack logging increased final median RSS by about8.48MiB/default or8.09MiB/min24,
with about3.9/3.7MiB additional malloc residency and175/159KiB live malloc, while
GC committed was unchanged. Profiler overhead is substantial and not a constant
that can simply be subtracted from another cohort.

Malloc dirty bytes are now retained separately. Some traced snapshots report
resident pages that have become clean: e.g.15.9MiB resident versus7776KiB dirty.
The tool's fragmentation column therefore must not be treated as RSS-minus-live
bytes. Reported dirty, resident, allocated and fragmentation values stay distinct;
rounding, scavenging and instrumentation effects remain visible. The new summary
can recompute these fields from the already captured numeric data.

These24wave controlled observations narrow the earlier short residual to
reversible worker-population effects and significant profiler/allocator behavior.
They do not prove general leak freedom, exact unknown allocator-frame identity,
a long uninstrumented soak or production/device acceptance.

Reference: https://learn.microsoft.com/en-us/dotnet/api/system.threading.threadpool
