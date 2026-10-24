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
