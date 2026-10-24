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


## Full source-level verification experiment

[Run40091348239](https://github.com/tryAGI/WebRTC/actions/runs/40091348239)
at99e98d2de4fbc428f0ba02b26565b6bfb0dd662e built the actual candidate compiler,
then compiled/executed/fully verified both the minimal repro and complete rooted
WebRTC smoke. The14 minimal and762 rooted overlap errors were eliminated in this
experiment. Minimal proof has exit0/errors{}/warnings{}; rooted debug-link proof
has dwarf_verified=true/debug_link_exit_code=0, official archive and native binary
unchanged by debug linking,19 relocated archive entries. Native binary SHA256
fd8bc102a11081097f0886b7a50a1816cecbdcebbfb6c2b78e1ffd66a527773c.
All14 nondebug sections/216symbols and the actual code-mutation rejection still
pass. This confirms the source-level candidate works for these full fixtures,
not every possible optimized EH layout, an upstream SDK release or production.

That experiment still has **seven upstream native-tool warnings** in source-build
logs despite MSBuild's0-warning summary: six duplicate-minipal link warnings and
one empty-bootstrapperdll archive symbol table. Overall workflow green therefore
is not zero-warning quality acceptance. The follow-up raw-log gate counts native
and managed diagnostics independently, retains original lines, and records source
diff/compiler payload hashes. Unit controls prove native warnings cannot be hidden
behind a0-warning summary. No diagnostic is suppressed.

The compiler-only follow-up builds clr.alljits+clr.tools rather than unused
nativeaotlibs/nativeaotruntime products (the repro uses the official SDK runtime).
It selects CMake's native library de-duplication policies
[CMP0156](https://cmake.org/cmake/help/latest/policy/CMP0156.html) and
[CMP0179](https://cmake.org/cmake/help/latest/policy/CMP0179.html), so redundant
archives are removed from link lines based on linker capability and first-use
order. This changes the actual link graph, not warning severity/output. A fresh
source build plus strict minimal/rooted verification must pass the raw-log gate
before clean acceptance; the earlier seven-warning experiment stays recorded.


## Clean final source-built result

[Run40092561235](https://github.com/tryAGI/WebRTC/actions/runs/40092561235)
at9be784893b6b9cdd883433000542ef7384f7b330 completed both jobs successfully.
The raw source-build diagnostic gate records **0 diagnostics**; an independent
scan of the entire compiler job's untruncated log also finds0 native/managed
warning/error diagnostic records. Structural flat/nested control passes again.
The compiler-only target/de-duplicated links remove the seven earlier warnings
without suppressing them. Minimal repro compilation/execution/fullverification
passes with errors{} and warnings{}. Complete rooted smoke compilation,
execution and full DWARF verification all pass. No verifier check was disabled.

Compiler identity: pinned runtime6aa72daa24f66c01c3d7b6a32519e9d21396deec;
complete source-diff SHA256a60411c7d75a85ff97031b20d4964436302f4096bfdaab36382ec554241ee45e;
17 compiler payload hashes retained, ilc executable SHA256
2663a1454d166dbf8fa6bca2ff17b50e550a244a5ab24af72a53d8f074527584.
The clean rooted native binary SHA256 is
dd1fa827ca2ed216b58449f2e64e6e69f8eee7cda77d107d07255e307f649a62.
Debug-only linking preserves that binary and the official framework crypto
archive, relocates19 debug archive entries and records dwarf_verified=true.
The14-section/216-symbol nondebug equality and real code mutation rejection hold.

This is a validated experimental source-level/debug-input repair for these
fixtures, with exact provenance. It is not an upstream released SDK, a replacement
for installed compilers, universal optimized-EH layout support or a production
voice/device acceptance result. Unsupported non-laminar layouts still fail closed.
The canonical publisher/consumer toolchain remains unchanged; promoting this
candidate requires upstream review/general compiler regression coverage.
