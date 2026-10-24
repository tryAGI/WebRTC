# Experimental source-level EH hierarchy correction

The pinned .NET10.0.13 runtime source at6aa72daa24f66c01c3d7b6a32519e9d21396deec
has the exact same DwarfInfo.cs bytes as monorepo4099f839 used by the diagnosed
package. The candidate groups duplicate try ranges, emits properly nested
contained native regions and marks EH abbreviations as containing children.
It preserves every distinct range/tag extent and uses iterative emission.
Crossing physical ranges, contradictory identical try/handler scopes and
out-of-bounds/overflow ranges explicitly fail. These cases need a richer native
range-list representation before any compiler compatibility claim.

The authored planner regression checks nesting/shared try grouping, distinct
region preservation, input-order independence, touching/empty ranges and rejection
of unsupported layouts. A separate assembly fixture feeds the real LLVM verifier
identical native range extents: flat siblings must fail, containment must pass.
This is structural verification, not whole-compiler acceptance.

The manual compiler-eh-candidate workflow also attempts the real upstream source
build, then publishes/executes/verifies the minimal repro and full rooted smoke
with the candidate compiler and official runtime/framework. Installed compilers,
SDK archives and runtime library code remain unchanged. Full DWARF verification
stays mandatory; neither debug entries nor warnings are removed to obtain a pass.
The experimental helper is not used by the library runtime or canonical publisher.

Source fixture owner/purpose/lifecycle: this workflow owns the stable
artifacts/upstream-runtime path in its ephemeral CI workspace, pinned to the
reviewed release commit. It is consumed only by this source-build experiment;
no local secondary checkout, global toolchain replacement or persistent source
cache is created. The runner destroys source/build outputs. Only candidate diff,
small proofs and original build logs remain30days. The45minute build bound is
finite; external failures are reported separately from candidate validation.

The adapted emitter/abbreviation boundary is MIT-licensed upstream .NET source;
file headers at the pinned revision were checked. Original license is retained
at tests/diagnostics/CompilerEhCandidate/LICENSE.TXT, SHA256
cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310.
DwarfInfo.cs SHA25634f1a169e73ef3e49b394a9b67bf1ed281ef7d9966c964067126cf5eb0bf2ade;
DwarfAbbrev.cs SHA2566ec1e2321754749656be87876eb5b16a778b9546416f4c80e4ad7269ad7da236.
The preparation script fails on either different input. The planner and structural
fixture are authored; no upstream crypto/protocol implementation is imported.
