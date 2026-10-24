# Allocation boundaries and physical cadence checks

Task ownership: the UDP receive source helper/loop and its protocol/AOT checks,
README allocation claims, experimental EH planner tests and physical diagnostic
scripts. Other repositories/work are preserved.

A zero-managed-allocation operation is not a zero-allocation library. Measured
warmed preallocated SRTP/SRTCP, selected parsers/STUN and empty-ciphertext GMAC
rejection have exact-zero contracts. Payload-bearing framework GCM rejection
still throws/allocates. The public .NET10 AesGcm API has no nonthrowing decrypt;
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
