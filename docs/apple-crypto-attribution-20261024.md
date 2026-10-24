# Apple crypto bridge and hostile-load attribution

The manual apple-crypto workflow separates the unchanged pinned .NET Apple crypto
Swift bridge from managed exceptions, sockets and scheduling. It builds one
Swift6 strict-concurrency optimized driver object, then links that same object
against the MIT upstream fixture built-Onone and-O, without LTO. It verifies the
fixture hash, uses three fresh processes per optimization, alternates order in
process2, and retains84 checked samples per process: AES128/256,0/60/1200byte
payloads, valid and bad-tag open, seven5000-operation samples after1000warmup.
The only imported source remains the already-attributed NativeSymbols fixture.
The authored driver borrows local array pointers synchronously, with no escaping
pointers or concurrent access. Bad tags must produce the bridge's authentication
failure classification and leave the output sentinel unchanged; valid opens must
match every plaintext byte. These are native timing observations, not allocation
counters, general security acceptance or a production replacement. The binaries
contain identical driver bytes but different bridge code by design. Neither SDK
archive nor WebRTC runtime is modified.

A separate macOS26 managed process runs the existing synthetic loopback workload.
The harness marks measured phase start after handshake/warmup, without packet or
session data. The profiler attaches only to its own child for3seconds at each
AES128GCM burst64/256 phase and preserves the workload exit code/partial CSV/full
stderr independently of profiler success. Instrumentation can change scheduling;
stack sample counts are inclusive and must not be summed into exclusive CPU time.
A missing phase or workload timeout fails, rather than manufacturing coverage.
The exact-source CSV/runtime metadata remains authoritative for this cohort.

All generated objects, binaries and stack captures are job-owned artifacts used
only by these diagnostics; jobs destroy compilation outputs after completion.
CSV/proof and synthetic stacks are retained30days for issues22/24. This introduces
no runtime dependency, persistent cache, SDK edit, provider call or device action.
