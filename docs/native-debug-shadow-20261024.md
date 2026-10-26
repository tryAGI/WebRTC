# Code-identical debug-input experiment

The original10.0.13 framework archive contains missing SDK-module debug imports
(issue20). Both exact-toolchain and exact-source attempts to recreate the original
module signatures failed. This experiment instead asks whether a freshly compiled
object from the unchanged, pinned MIT-licensed framework source can supply coherent
debug information for **identical** loaded code. It is not a runtime/linker substitute.

`verify-native-debug-object.py` compares every non-DWARF code/data section's bytes,
address, size, alignment, flags, normalized relocations and nondebug symbols. Other
load commands must match. The sole additional excluded section is the compiler's
16byte `__LLVM/__swift_modhash`, which represents module-debug identity; its layout
must match and it must have no relocations. The candidate object is only a dsymutil
input, so no changed module hash can enter runtime code. Unknown LLVM sections fail.
An actual mutated code byte must fail the same equivalence verifier.

The debug-only archive preserves every earlier archive member and byte offset,
replacing only the final Swift member after symbol/code equality has passed. Its
new debug imports must match the actual generated PCM signatures. The original
missing PCM is never mapped to incompatible module data. Only an explicit dsymutil
archive-path map consumes the candidate; framework files and linker inputs remain
unchanged and hash checked. The public proof contains hashes/counts/booleans only.

The manual job requires strict warning-as-error NativeAOT publication, complete
DWARF verification, usable crypto/managed function ranges and full rooted smoke
execution before any repair claim. A failed comparison produces no map. Existing
zero-warning requirements, cryptography and replay checks are unchanged.

The job-owned artifacts/debug-input directory is temporary compiler/debug data,
consumed only by this publish's dsymutil invocation and destroyed with the job.
Only proof JSON is retained30days; no SDK PCM/archive or raw symbols are uploaded.
Source/license provenance remains in docs/provenance.md and the adjacent .NET
fixture license. No persistent cache or new runtime dependency is introduced.


The first-O candidate failed code/data equality (run40063545459) and emitted
no map. The-Onone direct-module probe40063729912 recovered the original module
configuration keyHCUVWJDIE8ME, but its Foundation/shims signatures still differed.
The debug-object experiment now uses that unoptimized configuration while
retaining byte equality and actual matching candidate-module guards. A matching
cache key alone is never accepted as signature compatibility.

Mach-O linkedit commands store physical file offsets. The verifier now compares
their referenced function-start/data-in-code/linker-hint payloads byte-for-byte
instead of requiring identical physical offsets after DWARF-size changes.
Tests require moved identical linker hints to pass and changed hint bytes to
fail. All loaded section addresses, bytes, relocations and symbols still match
exactly or the candidate is rejected; structural difference proof now records
section names, counts and differing-field labels without code/address dumps.


Run40064373973 shows exact matching code/data bytes, section sizes and symbol
names; remaining differences were only the object bases of compact_unwind and
eh_frame plus their section-symbol values. In MH_OBJECT these are relocatable
metadata placed after DWARF, so their bases move with debug size. Only these two
unwind sections now normalize bases and symbol values to section-relative form.
Their bytes, alignments, flags and normalized relocations remain exact; all code
and data addresses remain exact. Tests accept a moved unwind base and reject a
changed symbol offset within the same section. This corrects the comparison
model; it does not authorize different loaded crypto code/data.


Run40065103651 passed the complete nondebug equality check and real code-byte
mutation control:14 sections,216 symbols, signature
cfa607327bfe734d32ed49572d11e2c6af6cf73db4a0eda90204c1518efe53ec.
Strict publication still reported both original missing modules: the initial
object-prefix map did not relocate the archive's debug-map entries.

The next probe uses LLVM's documented YAML debug-map input. A job-local dsymutil
wrapper dumps the real binary's map, changes only verified archive filename
entries, and invokes the real tool with-y and--verify. All diagnostic stderr and
final tool output/status remain visible; no warning is filtered or disabled.
The binary and official archive hashes must remain unchanged. Only the owned
NativeBinary is accepted, with no quiet/ignore/debug-disabling options. The
wrapper lives only in this publish step's PATH and uses the exact verified
archive identities. Native-link proof records the unchanged binary hash and
relocated-member count. A successful strict publish/full DWARF/smoke is still
required before claiming repair. No archive or PCM is uploaded.


The canonical validation/publication workflow now passes MSBuild-warnaserror
for build, NativeAOT publish and package creation. The prior green Mac lane
could still emit the two native-tool warnings; green status was not clean
quality evidence. New publication must fail closed on those diagnostics until
verified debug linking is integrated, rather than continue producing packages
through the known external blocker. Compiler warning settings are not weakened.

The first YAML-map run40065467451 removed both original missing-module
failures and reached full verification. Verification rejected762 overlapping
managed exception ranges in the generated il.cpp compilation unit. The job
remains failed; no verifier option or diagnostic was disabled. Those entries
belong to the managed NativeAOT debug stream, not the replaced Swift debug input.
An authored framework-only nested-exception fixture now isolates that compiler
boundary without a WebRTC reference. Its strict build/execution and unfiltered
verifier diagnostics are retained separately; failure is never converted to pass.


## Final experiment and compiler boundary

[Run40067547675](https://github.com/tryAGI/WebRTC/actions/runs/40067547675)
at4a235ced5d1c712151474d52bc0857670093bfc9 confirms the same14-section,
216-symbol equality and real mutation rejection. The official archive and native
binary hashes remain unchanged. Nineteen archive debug-map entries were relocated;
actual crypto and managed function-range queries both succeeded. Full DWARF
verification still rejected762 overlapping managed exception ranges, so strict
publication failed and rooted smoke execution was not reached in this experiment.
A tool version probe is also intentionally rejected by the wrapper's one-binary
argument restriction; that diagnostic is visible and is not quality acceptance.

The framework-only AotDwarfRanges fixture compiled with-warnaserror and executed
successfully, then failed unfiltered full DWARF verification with14 overlapping
exception-range errors. This independently reproduces the managed compiler defect
without WebRTC, Swift debug relocation or provider traffic. See
[issue23](https://github.com/tryAGI/WebRTC/issues/23). Debug-range usability is
strictly weaker evidence than valid complete DWARF; none of these failures is waived.

The separate original-path exact-source probe
[40067545494](https://github.com/tryAGI/WebRTC/actions/runs/40067545494) also failed
its strict original module-signature guard. Debug-prefix mapping did not reproduce
the original PCMs. No incompatible map, SDK cache edit or runtime replacement was
accepted. This remains a partial diagnostic repair, not a canonical AOT repair.
The original Swift bridge matching-Onone code is a separate optimization hypothesis
tracked in [issue24](https://github.com/tryAGI/WebRTC/issues/24), not an attributed
cause of audio loss. No Advantage dependency was changed in this batch.


## SDK probe and emitter follow-up

The SDK's actual capability probe is--help (confirmed in10.0.13
Microsoft.NETCore.Native.Unix.targets), in addition to the supported--version
identity query. The initial version-only correction did not address that probe.
The wrapper now delegates both exact single-argument queries, preserving complete
stdout/stderr/status. Every real binary link still enforces archive equality,
no diagnostic-disabling options and full DWARF verification. The105-test Python
suite includes wrong/mixed-option rejection and nonzero probe status forwarding.

[Run40076117151](https://github.com/tryAGI/WebRTC/actions/runs/40076117151)
at72c553ba4d43f8604074f927c54595e36bbd9bce has zero rejected-probe diagnostics.
It retains unchanged official archive/binary, usable crypto/managed ranges and
762 rooted managed overlap errors; the framework-only repro still rejects14.
Full quality remains failed. This fixes our probe handling, not the upstream
managed debug emitter.

Pinned framework source inspection gives a concrete upstream lead:
[DwarfSubprogramInfo.Dump at4099f839](https://github.com/dotnet/dotnet/blob/4099f83977f4d29604a9aeb971fb0a7bc1d5f699/src/runtime/src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/ObjectWriter/Dwarf/DwarfInfo.cs#L424)
emits each clause's try and handler as siblings under the method. Nested and
shared try ranges can overlap at that sibling level. This source observation is
consistent with the repro; a correct compiler fix needs native range grouping,
containment/hierarchy and shared-clause/funclet coverage with full verifier and
runtime tests. No debug entries were discarded, ranges rewritten post-link,
compiler replaced or full repair claimed. See issue23 for the upstream boundary.
