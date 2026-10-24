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


## Structural acceptance and source-build blockers

[Run40091104136](https://github.com/tryAGI/WebRTC/actions/runs/40091104136)
ata22eab7 compiled/executed the planner and passed the real LLVM positive/negative
control: flat verifier exit1; same extents nested verifier exit0. Local planner
compilation also passed0warnings/errors after fixing a JsonArray overload to use
a checked JsonNode boundary; local execution passed but211s admission/runtime
breached the host SLO. A premature local LLVM invocation lacked the not-yet-written
tree input and failed; this was a harness sequencing error, not verifier evidence.
The corrected local admitted run passed the same flat-rejection/nested-acceptance
control (receipt20261024-15443aaf18134b79906186a4d0357b9f,92.670s; SLO pass). Neither failed local attempt
is counted as a passing test.

The first full source restore failed4 audit errors in upstream build-task projects:
NU1902/NU1903, System.Security.Cryptography.Pkcs9.0.6. A tee pipeline accidentally
returned success for that build step; its next step correctly failed for absent
compiler output. The workflow now enables shell pipefail and retains complete
build output. The source adapter explicitly pins only those two build tasks to
9.0.21, the patched9.x release in both Microsoft advisories
[GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) and
[GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx).
Audit and warnings-as-errors remain enabled. These task dependencies are separate
from EH emitter changes and from the official10.0.13 runtime/framework used by
the repro. Their original project hashes are checked by the preparation script;
all source changes are retained in the CI diff under the upstream MIT notice.
No full compiler repair/acceptance follows from the structural fixture alone.
