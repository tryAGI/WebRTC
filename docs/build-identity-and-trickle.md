# Build identity and ready-state trickle

The current source adds two acceptance prerequisites for consumers such as Advantage,
DId and Simli. Neither is evidence of a successful provider or physical-device run.

## Identify the loaded library, not AssemblyVersion

```csharp
var build = WebRtcBuildInfo.Current;
// PackageId, PackageVersion, SourceRevisionId
var retained = peer.GetEstablishmentEvidence();
// retained.Build has the same identity, including after peer disposal.
```

MSBuild generates these values from the package version and source revision used by
MinVer and the .NET SDK. The runtime does not use reflection, load files or inspect the
caller's assembly. Public values are properties, not constants inlined into consumers.
The generated source is configuration/framework-specific, participates in clean, and
is rewritten only when its content changes.

`SourceRevisionId` is empty when the build has no repository revision. A revision
identifies a build input, **not** a clean-tree or binary-authenticity attestation:
local edits can share the same parent commit and version. Production acceptance must
also retain the serving deployment, exact published NuGet pin and package/assembly
hash proof. The existing `scripts/verify-package.py` checks the NuGet manifest's
repository commit, experimental version, framework, license and dependency graph.
Never infer a NuGet version from `AssemblyVersion` (for example `0.0.0.0`).

A consumer with an explicit serializer projection must copy `Build` into its own
safe DTO. Merely upgrading the library does not change a hand-written projection.
Keep this metadata separate from provider SDP, addresses, credentials and payloads.

The managed and NativeAOT acceptance case `BuildIdentityTests.Retained` can assert
exact inputs via `WEBRTC_EXPECTED_PACKAGE_VERSION` and
`WEBRTC_EXPECTED_SOURCE_REVISION`. Missing expected-input variables do not validate
a particular release; the default case only validates the API and retained shape.

## Candidates after offer/answer, before ConnectAsync

Once `CreateAnswer` or `SetRemoteAnswer` succeeds, `AddRemoteCandidate` accepts a
candidate in `Ready` as well as `Connecting`/`Connected`. This lets a signaling
adapter deliver a candidate immediately without racing the explicit connect call.
Before a successful remote offer/answer, the operation still fails. This does not
add multi-generation ICE restart or a signaling protocol.

Ready candidates are deduplicated by endpoint and bounded together with the
negotiated SDP candidates. A separate retained-state cap prevents a changing
application destination filter from creating an unbounded buffer. Address-family
and destination-policy checks remain active; the transport still bounds the actual
local/remote pair count. Canceling before connect preserves `Ready`; disposal clears
the buffer. Supplying candidates never starts connectivity checks by itself.

An empty negotiated BUNDLE tag does not fall back to candidates from another media
section. Tests cover an entirely empty list and a non-tag decoy with a higher
priority, both audio/data tag orders, both DTLS roles, and trickle before/after
connect. All 16 permutations exchange encrypted bidirectional Opus and DCEP data
using real local UDP sockets. They also run in the whole-library NativeAOT smoke.

Incremental gathering and checking can overlap when the signaling protocol supports
Trickle ICE ([RFC 8838](https://www.rfc-editor.org/rfc/rfc8838.html)); this API change
removes an application ordering restriction, not a measured Watch latency. Keep
regular/half-trickle fallback for peers without confirmed trickle support. Measure
provider establishment and device playback separately using the acceptance matrix.
