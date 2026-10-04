# WebRTC

Independent WebRTC implementation for .NET 10 and later, licensed under MIT.
Commit scoped logical batches directly to main. Preserve unrelated work.

## Source and dependency policy

- Write protocol implementations from the standards listed in docs/architecture.md.
- Do not copy or translate SIPSorcery source. Its current additional use restriction
  is unsuitable for this project's intended distribution.
- Any copied or translated implementation, test, fixture or asset requires a file-level
  license check at a pinned commit, original attribution and a provenance entry.
  MIT source is the preferred permitted import. Other permissive licenses require an
  explicit compatibility decision and their original notices; our MIT LICENSE does
  not replace upstream terms.
- Do not claim a clean-room implementation or security audit merely because code is
  newly authored or has passing tests.
- Runtime code currently depends only on the .NET shared framework. New NuGet/native
  runtime dependencies need a recorded rationale, source provenance and test scope.
- Target net10.0 only; avoid compatibility shims and reflection. Do not weaken AOT or
  trim diagnostics. Protocol-mandated algorithms require a documented justification.

## Validation

`dotnet build WebRTC.slnx -c Release`

`dotnet run --project src/tests/WebRTC.ProtocolTests -c Release`

`dotnet publish src/tests/WebRTC.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -o artifacts/aot`

Execute the resulting smoke binary. CI must also run the managed protocol/network
suite and publish/execute the whole-root `linux-arm64` smoke on a native aarch64
runner, matching Advantage's deployment architecture. Protocol tests must cover independent reference
vectors, malformed/truncated data and resource bounds. Interoperability uses local
peers and containers only in the default suite and all CI smoke lanes. The user
explicitly requires separate real provider and Watch E2E acceptance; use a separately
invoked acceptance lane for those sessions, never an unconditional CI/provider smoke test.

This repository is a protocol foundation until ICE, DTLS/SRTP and SCTP/DCEP peer
interoperability is demonstrated. Do not migrate DId, Advantage or Simli on parser-only
or ICE-only evidence. Track full completion against docs/acceptance.md, including
actual provider and device evidence before completing the active objective.
