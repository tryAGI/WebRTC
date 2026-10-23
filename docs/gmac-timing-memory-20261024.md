# GMAC verification, timing warm-up and residual memory controls

## Reproducible scope

Related issues: [GCM rejection #19](https://github.com/tryAGI/WebRTC/issues/19),
[benchmarks #15](https://github.com/tryAGI/WebRTC/issues/15),
[memory ownership #10](https://github.com/tryAGI/WebRTC/issues/10).

Before: `ea912c63424b60426c1828f9bc69dbc6933d2add`, runtime tree
`32c3f2bb23f719d236281908c31f8f589bfb674d`.
After: `72c2eafc2f1bed942a043b03dd703078703157e4`, runtime tree
`a438441fc04ddfeea8a1a5302de50bd3dccedbaf`.
The benchmark source changes only by enforcing the new zero gate between these
captures; existing payload-bearing rejection budgets are unchanged.

## Standard-backed empty-ciphertext verification

Empty-ciphertext GCM is GMAC, the authentication-only specialization in
[NIST SP 800-38D](https://csrc.nist.gov/pubs/sp/800/38/d/final).
[RFC 7714 sections 9.2/9.3](https://www.rfc-editor.org/rfc/rfc7714.html#section-9.3)
cover encrypted and authentication-only SRTCP, including empty ciphertext.
The receive cipher recomputes the expected 16-byte tag with framework
[AesGcm.Encrypt](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0),
using empty input/output and the received packet's nonce/AAD. It compares tags
with `CryptographicOperations.FixedTimeEquals` and clears stack tag storage in a
finally block. No generated tag escapes, no outbound packet is created, and no
plaintext is decrypted or released by this branch. Computing a received MAC does
not create an outbound nonce reservation. This is not a new encryption scheme,
custom GHASH, double-encryption decryption workaround or native ABI dependency.

Replay/index/framing/source limits and post-authentication state admission stay in
place. Nonempty ciphertext still uses framework authenticated decryption, whose
failed-tag exceptions retain the reviewed nonzero budgets. No transport-wide
zero-allocation claim follows from this specialization.

The synthetic GMAC suite covers both GCM key sizes and three packet shapes:
12-byte empty RTP, 8-byte encrypted RTCP receiver report, and 28-byte E=0 SRTCP.
E=0 fixtures are the existing independent Pion v3.1.3 vectors, generated from
owned synthetic inputs (pinned source/provenance in `SrtpTests`). Each shape has
bad-tag, wrong-key and preaccepted replay cases; seven samples of 1,000 operations
in three fresh processes/compiler/platform produce 378 exact raw totals.

The shared protocol/NativeAOT contract additionally flips every one of the 128
tag bits, requires rejection without changing sentinel output, and then accepts
the valid packet at that same source/index. Wrong-key recovery uses a correct-key
packet at the same source/index; for E=0 input the recovery packet uses E=1,
exercising their shared replay state. Existing independent vectors mutate every
packet byte, then accept the original E=0 packet and reject its replay.

Reproduce with `--suite gmac` and retain files `gmac-1.csv` through `gmac-3.csv`.
`python3 scripts/check-gmac-budgets.py RESULTS --zero --output proof.json`
requires exactly zero in every sample. Omitting `--zero` is baseline capture only,
never acceptance. A one-byte regression cannot disappear through rounding.

## Timing warm-up

Crypto and packet suites now run repeated batches for at least 500 ms and observe
a five-batch range within 10%, bounded by 128 batches or 3 seconds. A short sleep
between batches gives background compilation a chance to run. This is observed
stability, not evidence that every JIT tier has finished. Warm-up comments record
case identity, rounds, elapsed time, minimum/maximum batch time and stability.
SRTP sequence numbers wrap normally with increasing rollover counters; warm-up
and measurements never reuse an outbound packet index.

The JIT/AOT comparator requires one complete, unique warm-up record per workload,
retains measurement sample spreads and omits a ratio if either warm-up remains
unstable or subsequent sample spread exceeds 20%. It does not hide instability, disable production tiering, change
allocation limits or impose noisy absolute timing gates. Compiler/hardware/source
matching, exact zero guards and injected-allocation controls remain mandatory.

## Paired residual-memory controls

The memory workflow defaults to paired diagnostic controls for all five modes:
empty harness, crypto, identity, audio peer and open-channel peer. Each default
and child-only tiering-disabled cohort has three fresh 64-wave processes; numeric
native captures are phase-matched at disposed waves 2/16/32/63. That is 30 processes
and 1,920 disposed-wave records. The existing peer-root checks remain required.

Default/off pairs now run on the same physical hosted runner, alternating order
in repetition 2; every child remains a fresh process.
The final comparison recomputes summaries from all numeric captures and checks
source/runtime/architecture/CPU-count/workload identities strictly within each
control pair; different workloads retain their own reported CPU-count cohort. Host load and time are not controlled, and different workloads may use different
runner models; no causal per-owner attribution follows. It reports per-control ranges of
change/tail slope and final phase medians for malloc resident/live allocated/
fragmentation, GC committed and RSS. Raw native addresses/paths are not published.
A difference between controls is not attribution to a particular native owner.
The original split-runner capture uses baseline `ea912c6`; the corrected paired
run records its own exact harness/source identity with the GMAC runtime tree.
The earlier empty-harness finding showed that disabling tiering can move memory
earlier without reducing final footprint; the paired peer cohorts test how far
that observation generalizes. No production runtime setting is changed.

## Validation

Python guard/controller/comparator tests: 67 passed locally. The local compiler
request waited for host capacity and was cancelled before admission; it did not
compile or execute this batch. Hosted exact-source checks and measured results
are recorded below after completion.

### Baseline and initial after evidence

[Baseline capture 40040571398](https://github.com/tryAGI/WebRTC/actions/runs/40040571398)
passes all four platforms. Before the specialization, actual GCM authentication
failures allocate 192–464 managed bytes/operation in JIT cohorts (depending on
platform/workload/process phase), and 240 bytes in NativeAOT. Preaccepted replay
is separately zero. This is not a payload-bearing decryption optimization.

The new timing warm-up observes stability in all 21 workloads for both compilers
on Linux x64, Linux arm64 and Windows x64 in that capture. macOS records only
9/21 stable JIT and 13/21 stable AOT cases, with 7 jointly comparable cases;
ratios are omitted for the rest. This is useful evidence of residual noise, not
a reason to weaken the allocation gates or claim all JIT compilation is finished.

### Rejected split-runner comparison

[Initial memory run 40040573865](https://github.com/tryAGI/WebRTC/actions/runs/40040573865)
assigned a 3-core runner to default audio and a 5-core runner to tiering-off audio.
The comparator rejects those different reported cohorts, even though individual
process/disposal checks pass. They are not pooled or normalized by overriding
processor count. The corrected workflow keeps both controls on the same runner
and alternates order; the rejected original captures remain available separately.
