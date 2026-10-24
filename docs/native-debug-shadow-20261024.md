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
