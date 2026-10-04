# Package publication

`tryAGI.WebRTC` targets `net10.0` and is MIT licensed. Every normal push to `main`
runs the complete validation workflow, then packs and publishes a development
version. Release tags use `v0.x.y`; MinVer derives the exact version from Git.
Untagged main builds use `dev` prerelease identifiers and Git height. A release tag
produces its exact package version and a GitHub release with generated notes.

Versions are restricted to `0.x` in both MSBuild packing and package verification.
A `1.x` tag, CLI version override or wrong-version nupkg cannot pass publication.
Development packages are experimental; even an unqualified `0.x` release does not
claim production readiness or complete provider/device support.

The publishing job depends on all six validation jobs at the same source commit:
three platform build/protocol/local-network lanes, executed whole-rooted NativeAOT,
independent Pion and Chromium decode/loss recovery. PRs run the same validations
without publishing. Missing, skipped, cancelled or failed jobs block publication.

The produced nupkg must declare the exact Git commit, correct ID/repository/MIT
license/readme, and only the `net10.0` runtime assembly. Verification refuses NuGet
runtime dependencies, native runtime assets and embedded build/tool assets. The
retained artifact includes the package, version, commit and assembly SHA-256 proof.

Reruns accept an existing immutable version only after downloading it from NuGet
and verifying its source and assembly hash against the current package. A different
assembly/commit fails; it is never overwritten or silently skipped. Upload results
can precede NuGet search indexing; check the flat-container payload for acceptance.

The existing `NUGET_KEY` must be available to this repository's publishing job.
If it is unavailable, publication fails explicitly after retaining the validated
artifact; an owner must enable repository access to the secret. No credential is
stored in source or copied from another repository.

Provider/hardware sessions are separate, explicitly invoked acceptance work.
Published packages and green CI do not satisfy DId/Advantage/Simli real E2E or Apple
Watch audibility/latency acceptance. See [completion gates](acceptance.md).
