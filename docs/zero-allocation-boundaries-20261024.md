# Allocation boundaries and physical cadence checks

Task ownership: the UDP receive source helper/loop and its protocol/AOT checks,
README allocation claims, experimental EH planner tests and physical diagnostic
scripts. Other repositories/work are preserved.

A zero-managed-allocation operation is not a zero-allocation library. Measured
warmed preallocated SRTP/SRTCP, selected parsers/STUN and empty-ciphertext GMAC
rejection have exact-zero contracts. Payload-bearing framework GCM rejection
still throws/allocates. The public .NET 10 AesGcm API has no nonthrowing decrypt;
no custom GCM, reflection, private framework interop or rejection cache is added.
Cold initialization, asynchronous owned packet/frame outputs, cancellation,
sockets and tracing have distinct costs. Native allocations remain separately
unmeasured; changing a README cannot turn those into zeros.

The host UDP source optimization uses one framework SocketAddress per receive
loop and caches one independently materialized source endpoint. Equal raw
addresses reuse it; address/port/IPv6 scope changes create a fresh endpoint.
Mutable receive memory never escapes or changes a retained source. It is source
resolution reuse, not media authentication; original ICE route/consent/size
checks and public owned-buffer lifetimes remain. Helper source is linked into
focused protocol/AOT regression fixtures; actual product routing remains covered
by independent network/relay tests. Caller/frame arrays remain nonzero costs.

Physical diagnostic owner/purpose/lifecycle: this task owns the stable ignored
artifacts/physical-cadence-20261024 directory. prepare-physical-dotnet.py installs
only the published Microsoft.NETCore.App10.0.13 osx-arm64 runtime there, verifying
its official release-metadata SHA512 and bounding download64MiB. Its dotnet host
runs synthetic loopback/timer canaries; no global PATH, SDK, CLI/adv or production
setting is replaced. It is retained locally with its canary output for review,
then may be removed by this diagnostic owner after30days if no consumer is active.
It is not a repository source copy or a product dependency. Every compiler and
local benchmark invocation still uses host admission. No provider/audio playback,
physical Watch/iPhone session or S3/AWS operation is authorized by this fixture.


The private runtime download uses the macOS system CA bundle with full TLS and
hostname verification. Initial Python-default trust-store failure was corrected
by selecting that trust bundle, never by disabling certificate checks. The
published runtime SHA512 must still match the official release metadata.


If source compilation cannot enter the busy local queue, physical-canary-build
creates an exact-source, warning-as-error CI test artifact with per-file SHA256.
It is downloaded into the same owned physical diagnostic family and verified by
run-physical-cadence.py before execution. It is a small precompiled test payload,
not a source copy, CLI replacement or installed package. Runtime/performance input
diffs against its committed source must be empty; raw canary metadata must agree.
Physical execution remains admitted even when compilation occurred in CI.


A second owned physical-artifact lane may use
`~/.cache/tryagi-webrtc/physical-cadence-20261024/<source>` on an owned arm64 Mac.
Only the hash-verified CI DLL/config payload and exact committed diagnostic/checker
scripts are transferred, never repository sources. The consumer is this task's
admitted synthetic canary, using its recorded installed .NET runtime without
upgrading it. Refuse existing output paths; keep raw captures and their runtime,
source and physical-host metadata. The same diagnostic owner may remove these
artifacts after30days only after confirming no active consumer. The standalone
runner requires a source manifest, validates every payload hash and checks observed
CSV source identity. This permits physical measurements without cloning a repo or
compiling on another host; it does not make unlike runtimes a matched cohort.


## Matched UDP allocation result

Before [40094711850](https://github.com/tryAGI/WebRTC/actions/runs/40094711850)
uses 2825a8d68bd7e3eebdcb2912e8669d0bb6b4cbc9; after
[40094714319](https://github.com/tryAGI/WebRTC/actions/runs/40094714319)
uses be642657c60d6634315eea73a77d6a3e0410a9a2. Both four-platform runs pass.
Every platform uses.NET 10.0.13, workstation GC and the same reported OS/CPU-count
cohort before/after. macOS 26.6.2 arm64 reports 3 CPUs; Windows 10.0.26100 x64 and
Ubuntu 24.04.5 LTS x64/arm64 report 4. Hosted hardware identity and load are not
controlled; no latency improvement or physical-audio result follows.

Each cohort has 1,440 measured rows: 96 cells across four platforms, three fresh
processes per platform and five samples per cell/process (15 observations/cell).
All 96 allocation medians decrease; reductions span 136.88–147.00 managed bytes
per packet. This is a whole-process loopback harness metric including receive and
diagnostic work, not an isolated cryptographic allocation measurement.

| AES-256-GCM, tracing/token off | 60-byte payload before → after B/packet | 1000-byte payload before → after B/packet |
| --- | ---: | ---: |
| Linux x64 | 840.22 → 696.09 | 2728.28 → 2584.28 |
| Linux arm64 | 840.09 → 696.28 | 2728.28 → 2584.28 |
| macOS arm64 | 840.28 → 696.28 | 2728.28 → 2584.28 |
| Windows x64 | 839.72 → 694.97 | 2727.72 → 2583.34 |

The stable-source helper itself measures0B across 10,000 warmed resolutions;
address/port changes and IPv6 scope changes retain independent previous endpoints.
The product still creates owned packet/frame arrays and other asynchronous state.
Those costs cannot be advertised as zero by applying this helper result to them.

[Functional run 40094716975](https://github.com/tryAGI/WebRTC/actions/runs/40094716975)
passes 8 of 9 lanes, including both native Linux architectures' independent Pion
interop, managed network/protocol lanes, Chromium and Linux NativeAOT. The stock
macOS NativeAOT lane fails the existing two missing-SDK-module native diagnostics
under warnings-as-errors. The separate source-built compiler/rooted experiment
40095638357 passes with the new source cache included. These are distinct evidence
boundaries; stock macOS publication quality remains blocked, not 9/9 clean.

## Physical execution status

The exact-source payload from
[40096189869](https://github.com/tryAGI/WebRTC/actions/runs/40096189869), source
06837d4486606583296b356a1250674a12168e57, compiles with warnings-as-errors.
DLL SHA256: tryAGI.WebRTC
`a685d8aaa9dc6760a02005313f0812c17515785dd731db9addb7c80dd718d5a4`;
performance harness
`8ba526f47898406306545457502a2ac4715363f52497854a97d8109a8bd86e21`.
The local physical Mac's two fresh compiler attempts and one precompiled execution
attempt each timed out at host admission around 301 s; no canary process started.
That is an admission blocker, not a passing physical cadence or compilation result.
The precompiled admitted artifact lane permits a separate owned physical Mac
measurement with its actual installed runtime recorded; unlike runtime versions
remain separate cohorts. Raw outputs and full verification must be inspected
before any physical result is asserted.

Payload-bearing GCM bad-tag rejection remains 192 B/failure in the measured warmed
JIT cohorts from the earlier timer/rejection attribution experiment. The public
framework Decrypt API throws on authentication failure; no nonthrowing AesGcm
open API is available in the target framework. A different crypto implementation
or weaker authentication is outside this optimization. See
[framework API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.decrypt?view=net-10.0)
and [attribution](timer-rejection-isolation-20261024.md). Existing exact-zero
contracts remain scoped; the README deliberately makes no whole-library zero claim.


## First physical synthetic capture: not cadence acceptance

The verified CI payload above executed on a physical arm64 Mac
(macOS 27.0.1, 10 reported CPUs, hypervisor flag 0), with its installed.NET 10.0.8
and workstation GC. It is a different runtime/host cohort from CI10.0.13;
physical transport latency must not be compared causally with hosted benchmarks.
The payload and diagnostic-transfer SHA256 manifests were checked before use.
The transfer contained DLL/configs and four small diagnostic scripts only.

One complete timer process recorded 300 wakes (100/mode) requesting 20 ms:

| Primitive | Median ms | p95 ms | Maximum ms | Gaps over 40 ms / 100 |
| --- | ---: | ---: | ---: | ---: |
| Task.Delay | 167.933 | 172.286 | 181.348 | 97 |
| PeriodicTimer | 33.143 | 168.730 | 173.998 | 49 |
| Thread.Sleep | 166.960 | 170.404 | 171.278 | 88 |

Wall elapsed and monotonic elapsed were retained. Periodic ticks can coalesce;
its lower median is not evidence of a reliable 20 ms cadence. This timer process
has no network or cryptography, so the observed wake delay does not require
GCM rejection traffic. Host load, process scheduling/priority and runtime version
are not isolated causes by this one physical capture.

The first authentication-load scenario was AES-CM, burst 0. ICE/DTLS setup and
warm-up completed (the measured-phase marker is present). Sample0 delivered
100/100 valid packets, with 0 injected/rejected packets and 0 ICE/media/fixture
drops. Elapsed 15323.312 ms; send gap minimum 23.737 ms, maximum 183.069 ms;
accepted receiver latency p50 0.626 ms / p95 7.566 ms / p99 20.893 ms.
The original 30-second scenario deadline then canceled execution before sample 1
completed. The process exits 1 and the canary fails; there is no full rejection
cohort, three-process physical acceptance, or audible/device result.
Do not increase the deadline or call the one completed sample a cadence pass.

The original runner's final proof has an empty repetitions list because it only
appended completed pairs; separate timer proof/raw CSV and failure stderr retain
the partial evidence. The runner now appends a repetition before executing it,
so future failed captures preserve completed/failed suite exit codes. Additional
ICE/DTLS/warm-up/sample stage markers are emitted outside measured allocation
windows; the 30-second deadline and allocation budget semantics are unchanged.
No runtime, power setting, timer resolution or active audio was changed.
