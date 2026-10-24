# Native symbol recovery experiment

Issue20's missing debug imports identify Xcode16.4 / Swift6.1.2 / macOS15.5,
framework native archive SHA25656819aa278060142966ae19af836277d0a5a74fd0e53d9497aab44552d6fad67,
and module DWARF signatures Foundation6baccd36ecae4f32 and
SwiftConcurrencyShimsb8de9adb4227fa2e. The archive's package metadata pins
https://github.com/dotnet/dotnet/commit/4099f83977f4d29604a9aeb971fb0a7bc1d5f699.
Its native Apple CMake command compiles Swift with full module debugging.

The authored tiny ModuleProbe.swift fixture generates actual SDK Clang module
objects using that exact compiler/SDK and build options. No upstream source is
imported. Its object is never linked into WebRTC and never replaces framework
code. `prepare-native-symbols.py` accepts only the reviewed official archive,
requires actual regenerated imported-module DWARF signatures to match, and emits
explicit dsymutil object-prefix maps to those verified files. Missing or different
signatures fail; no renamed placeholders, removed symbols, ignored diagnostics,
patched SDK cache or substituted crypto are permitted.

The job-owned `artifacts/native-symbols` directory exists only for this CI job;
its consumer is the SDK's documented `DsymUtilOptions` property at native publish.
No persistent cache is introduced. Only numeric/signature proof JSON is uploaded,
not SDK module data or raw symbol paths. A successful experiment must still
execute the full AOT smoke, verify linked DWARF and demonstrate clean tool output
before being integrated into ordinary build lanes. Until then issue20 remains
blocked; reconstructing matching imports is a candidate, not a repair verdict.

References:
- https://www.llvm.org/docs/CommandGuide/dsymutil.html
- https://github.com/dotnet/dotnet/blob/4099f83977f4d29604a9aeb971fb0a7bc1d5f699/src/runtime/src/native/libs/System.Security.Cryptography.Native.Apple/CMakeLists.txt
- https://github.com/actions/runner-images/blob/main/images/macos/macos-15-arm64-Readme.md

## Exact-toolchain result

[Run40059309340](https://github.com/tryAGI/WebRTC/actions/runs/40059309340)
used the real Xcode16.4 compiler and macOS15.5 SDK. It failed the signature
check: regenerated Foundation was `c8d25a45d619ab5c`, while the official
archive imports `6baccd36ecae4f32`. The compiler version alone does not
reconstruct compatible module debug data. No path map was emitted, and no
framework or runtime implementation was modified. Native symbol acceptance
remains blocked by issue20. A corrected official framework pack containing its
module debug data is the preferred repair; changing names or weakening the
signature check would misrepresent the available type information.
