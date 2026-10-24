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
