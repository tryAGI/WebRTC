# Experimental source-level EH hierarchy correction

The pinned .NET 10.0.13 runtime source at6aa72daa24f66c01c3d7b6a32519e9d21396deec
has the exact same DwarfInfo.cs bytes as monorepo4099f839 used by the diagnosed
package. The candidate groups duplicate try ranges, emits properly nested
contained native regions and marks EH abbreviations as containing children.
It preserves native PC coverage for every distinct original range/tag and uses
iterative emission. The original laminar-only candidate rejected crossing and
equal-extent mixed-tag ranges. The broader candidate below partitions those
physical ranges into nested fragments; invalid bounds/overflow still fail closed.
Native scope coverage is not a reconstruction of source-language lexical nesting.

The authored planner regression checks nesting/shared try grouping, distinct
region preservation, input-order independence, touching/empty ranges and rejection
of invalid layouts and excessive fragmentation. Separate assembly fixtures feed
the real LLVM verifier the same original native PC coverage: flat siblings must
fail, nested/partitioned coverage must pass.
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

The first full source restore failed 4 audit errors in upstream build-task projects:
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
archive, relocates 19 debug archive entries and records dwarf_verified=true.
The 14-section/216-symbol nondebug equality and real code mutation rejection hold.

This is a validated experimental source-level/debug-input repair for these
fixtures, with exact provenance. It is not an upstream released SDK, a replacement
for installed compilers, universal optimized-EH layout support or a production
voice/device acceptance result. That earlier candidate still rejected non-laminar
layouts; the broader experiment below supersedes that limitation within its bounds.
The canonical publisher/consumer toolchain remains unchanged; promoting this
candidate requires upstream review/general compiler regression coverage.


## Broader physical-range support

The boundary-sweep fallback retains each original range/tag identity and covers
each of its original native PCs exactly once, even when two physical scopes cross
or a try/handler share the same extent. It fragments ranges only at existing
boundaries, emits non-overlapping siblings and contained children, and preserves
empty regions. The fast laminar path remains. Invalid method bounds/overflow and
more than 16,384 fragments fail closed; this finite bound deliberately excludes
pathological inputs rather than allowing unbounded compiler work.

Four authored coverage scenarios include nested/shared tries, crossing ranges
with empty scopes, equal-extent mixed tags and nested crossings. Each checks every
PC against its original scope identity, parent containment, sibling separation
and empty-scope retention. A 300-range hostile case must exceed the fragment bound.
All four independent assembly controls reject the flat original scopes and pass
the partitioned hierarchy in LLVM (flat exit1, candidate exit0).

[Run40095638357](https://github.com/tryAGI/WebRTC/actions/runs/40095638357), source
5de69a367d2566968816c3d917ceae3389a925e8, passes both the structural job and real
source-built compiler/minimal/full-rooted job. The full rooted smoke includes the
UDP source-cache regression. Source build has 0 raw diagnostics; minimal compile,
execution and full verification pass with empty warning/error sets. Rooted
execution/full DWARF verification pass; debug linking preserves the binary and
official crypto archive and relocates 19 archive entries. The 14-section/216-symbol
nondebug equality and actual code-mutation rejection still hold.

The previous broader attempt40095025619 failed compiler compilation on CS8632
because the upstream file disables nullable annotations. The corrected helper
explicitly enables nullable contracts only within the authored region and restores
the upstream context afterward; no diagnostic suppression or nullable disable was
added. That failed attempt is not counted as passing compiler validation.

Pinned runtime source remains6aa72daa24f66c01c3d7b6a32519e9d21396deec.
Broader source-diff SHA256:
5c895e29318803c3373cdcde5131f78e949f43c88e6a9fbd4d68499fbb3f66a7.
ILCompiler.Compiler.dll SHA256:
6f9843a4949494ad3d922c172ee1ab0076e6179ee64405da63368c51724f6ed2.
Rooted native binary SHA256:
3b8185b435967d561de534947f551d02be507b89434ff4715f4942cbae11dde9.
The launcher ilc hash is unchanged; the compiler assembly/diff identify this
broader candidate. These are experimental native coverage/debug results, not an
upstream SDK release, universal source lexical correctness or device/audio proof.
The installed SDK and canonical publisher remain unchanged.


### Leaf-scope diagnostic correction

A full independent diagnostic scan of40095638357 finds 0 diagnostics in the
compiler job, but the structural fixture emits warnings for leaf try/catch scopes
that declare children and have none. Therefore its exit 0 positive fixtures are
**not** a zero-warning structural quality pass. The 38 diagnostic records in the
hierarchy job include deliberately failing overlap controls, aggregate summaries
and these real positive-fixture warnings; they are not 38 new compiler defects.

The follow-up keeps the upstream leaf abbreviations as DW_CHILDREN_no and adds
separate parent abbreviations only when children exist. The assembly fixture
models the same distinction and fails any positive-case warning/error diagnostic,
even when the verifier exits0. Negative overlap controls retain full diagnostics.
This fixes the represented tree rather than suppressing warnings. Fresh source
compiler and structural verification are required for this final candidate.


### Final broadened candidate: clean positive controls and rooted verification

[Run40097448470](https://github.com/tryAGI/WebRTC/actions/runs/40097448470), source
c01af501474f4d7ea13bf34b5ce843bb85d0c4d5, completes both jobs successfully.
All four positive structural fixtures exit0 with0 diagnostic records. Their flat
negative controls exit1 and retain4/3/3/4 error/aggregate diagnostic records;
negative-test errors are expected and are not hidden. Source build and an
independent scan of the complete compiler job both find0 raw diagnostics.
Minimal compilation/execution/full verification pass with empty warnings/errors.
Full rooted compilation/execution/DWARF verification pass, with unchanged official
archive/native binary during debug linking,19 relocated entries and the existing
nondebug equality/code-mutation negative control intact.

Final source-diff SHA256:
8f0e07248f4fc718c0c5b820d27f27dc5db94b9d0330b9bb0715cd90e2e120bf.
Final ILCompiler.Compiler.dll SHA256:
ba672c6fbc949fc3ac5b7fa1998749c1ba20cdf516444b39822ebeb4f8738e50.
Final rooted native binary SHA256:
8e2667fc67225e6b00ef0eafc92e80c4b929e5b8bf9a5bfe777a25e88ec74d3a.
Pinned upstream source/runtime and all experimental/canonical SDK acceptance
boundaries above remain unchanged. This supersedes the leaf-warning limitation
of the previous structural run, not its retained failure history.


## Debugger semantics: independent execution fixture

A new authored framework-only NativeAOT fixture exercises try/catch/finally with
five no-inline checkpoints. LLDB must launch it, resolve one native breakpoint,
read each caller's source line and active outer/scope-local values, observe exact
checkpoint arguments and reach normal exit0. Full DWARF verification remains a
separate mandatory gate. A real debugger error leaves a failed proof and complete
log; an exit0 LLDB process alone cannot manufacture semantic acceptance.

The first candidate run40099785218/source22360150e2507a12484dc293bce763157be1054f
passes source compilation and full fixture DWARF verification, but fails LLDB
semantics. At the automatic source/prologue breakpoint, caller line17 and locals
outer101/tryOnly111 are correct; checkpoint arguments are112 and an unexpected
pointer-sized value instead of1/111. This is a real failed observation, not a clean
semantics pass. Register reuse after optimized arguments' final use is a hypothesis
until entry/source controls and instruction evidence are compared.

The next checker explicitly selects the native function entry for argument reads,
while checking caller locals/source mapping at the call site. It does not claim
that optimized source breakpoints show expired parameters correctly. A separate
unchanged official-compiler baseline checks both entry and source locations with
instruction offsets retained. The source-location check stays fail-closed even if
entry behavior passes; these two results must not be conflated or attributed to
the EH hierarchy candidate without the official control.
