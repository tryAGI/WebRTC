# STUN allocation contracts and phase-matched native memory

The owner requested wider allocation contracts and zero allocation where safe,
with native profiling at equivalent lifecycle phases.

## Runtime change

Before: `836d19d04ff083784d94c998349728c815b1ac5c`.
Runtime change: `40f9725280ef440f28fb489005cc9fc181daf2ee`.
Package validation source: `f091f6f7627d3941cd1bf0ec714b260707dab8c2`.
Final profiling harness: `a1097a9cfa4b7433f1e1f99c676a5414a1d76dc0`.
The latter two commits only change profiling tooling/workflow/tests. All three
have identical runtime tree `32c3f2bb23f719d236281908c31f8f589bfb674d`.

STUN verification previously constructed an incremental framework HMAC object
per call. It now uses the same framework one-shot HMAC API as the writer, with
at most 2 KiB stack input and cleared/returned framework pool storage for larger
valid messages. This preserves caller storage, strict attribute envelopes,
RFC-adjusted authenticated length and constant-time tag comparison. No custom
cryptography or new runtime dependency was introduced.

Existing independent SHA-1/SHA-256 vectors remain mandatory. New boundary tests
cover authenticated input lengths immediately below/above the stack limit and
maximum framed messages. Ordinary protocol tests also guard replay, truncation
and short-destination SRTP/SRTCP rejection for every profile, plus bad-tag AES-CM
rejection: caller output stays unchanged and later valid authentication succeeds.
AES-GCM bad tags use an exception-based framework API and remain outside the
zero contract; measurement and safe alternatives are tracked in [#19](https://github.com/tryAGI/WebRTC/issues/19).

## Exact-byte measurements

[Before four-platform run](https://github.com/tryAGI/WebRTC/actions/runs/40025745903),
[after run and native profiling](https://github.com/tryAGI/WebRTC/actions/runs/40027503513).
Both preserve raw CSV and runtime/source/hardware identity. All use .NET 10.0.13;
CPU count, OS family and architecture match between each platform's before and
after jobs. Separate hosted jobs do not establish matched timing conditions.

Small valid and wrong-key authentication share these median managed bytes/call:

| Platform | Before SHA-1 | Before SHA-256 | After SHA-1 | After SHA-256 |
|---|---:|---:|---:|---:|
| Linux x64 | 216 | 224 | 0 | 0 |
| Linux arm64 | 216 | 224 | 0 | 0 |
| Windows x64 | 264 | 272 | 0 | 0 |
| macOS arm64 | 264.01 | 272.01 | 0 | 0 |

Each after platform guards 147 exact raw-zero samples: 42 crypto and 105 packet
samples. Packet coverage now includes plain/SHA-1/SHA-256 writes and valid/wrong-key
verification with small and warmed input containing a 4 KiB attribute. Every sample has 20,000 operations
and seven repetitions; crypto retains 5,000 roundtrips/sample. Exact totals, rather
than rounded bytes/op, decide the verdict. The real allocation injection still
must be rejected. All 40 active workload ceilings remain unchanged.

These are warmed synchronous managed allocation contracts, not whole-transport
zero allocation. A cold pool miss or concurrent pool exhaustion can allocate.
Native crypto/allocator resources and owned asynchronous outputs remain separate.

## Phase-matched native profiling

The final four-platform benchmark run passed, including 147 exact-zero samples,
42 rejected real allocation controls and all 40 unchanged active budgets per
platform. Every performance build reported zero warnings/errors. All 42 local
Python checks pass. The first two profiling attempts failed on a parser assumption
about a total row, then the corrected single-zone/aggregate parser passed. No
failed attempt is counted as native-memory acceptance.

Four fresh macOS arm64 processes separately ran empty harness, four identities,
two audio peer pairs and two open-channel pairs (trace off). Each ran four warmup
plus 24 measured waves. Captures explicitly waited at waves 2 and 16 after
peer disposal and bounded GC/finalizer checks; every disposed peer weak reference
was dead in all 24 records. No suspend/resume signals were used. Only allowlisted
numeric allocator aggregates and CSVs are retained publicly; raw vmmap process
identifiers/addresses/paths stay out of artifacts.

`vmmap` values below retain its rounded binary-unit notation (K/M), not exact
byte precision. Early/late values are phase matched within each process.

| Cohort | Malloc resident, wave2→16 | Live malloc bytes, wave2→16 | Free/fragment space, wave2→16 | GC committed bytes (both captures) |
|---|---:|---:|---:|---:|
| Empty harness | 4000K→4256K | 1369K→1653K | 2631K→2603K | 360448 |
| Identity only | 11.8M→11.9M | 2271K→2058K | 9761K→9.9M | 360448 |
| Audio peers | 13.7M→17.9M | 10.2M→3855K | 3649K→14.1M | 4816896 |
| Open-channel peers | 13.8M→18.0M | 7834K→3987K | 6294K→14.1M | 5865472 |

Open-channel process RSS rose 87,638,016→94,027,776 bytes while GC committed was
constant. At the same disposed-peer phase, malloc resident grew while reported
live malloc bytes fell and free/fragment space increased. This is direct finite
cohort evidence of allocator retention/fragmentation contributing to resident
memory evolution. It does not attribute every allocation owner, account for all
RSS, prove a stable long-run plateau or establish native leak freedom. Profiling
itself perturbs timing; repeat-process/longer attribution remains [#10](https://github.com/tryAGI/WebRTC/issues/10).

The existing runtime-compatible nine-lane validation passed in
[40027032611](https://github.com/tryAGI/WebRTC/actions/runs/40027032611).
NativeAOT performance comparisons remain [#15](https://github.com/tryAGI/WebRTC/issues/15).
