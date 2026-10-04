# Contributing

Contributions, interoperability reports and focused protocol fixes are welcome.
Start with an issue or discussion for a substantial public API or transport change.
Use [bug reports](https://github.com/tryAGI/WebRTC/issues/new/choose) for reproducible
failures and [Discussions](https://github.com/tryAGI/WebRTC/discussions) for questions.
Security findings belong in [private reports](SECURITY.md).

## Development

Use .NET SDK 10, Git and Python 3. Docker is needed for independent Pion/Chromium
checks. The runtime targets `net10.0`; no older-target compatibility shims are planned.

```sh
dotnet build WebRTC.slnx -c Release
dotnet run --project src/tests/WebRTC.ProtocolTests -c Release --no-build
dotnet run --project src/tests/WebRTC.IntegrationTests -c Release --no-build
python3 -m unittest discover -s scripts -p 'test_*.py'
dotnet list WebRTC.slnx package --outdated --include-transitive
dotnet list WebRTC.slnx package --vulnerable --include-transitive
```

For protocol changes, run the relevant independent peer checks and the executed
NativeAOT lane described in the [README](README.md). Default tests use local peers,
synthetic credentials and disposable containers. Never add a paid/provider endpoint
to default CI. Record skipped or unavailable gates; passing a selected test subset
is not evidence for the complete suite.

## Source and dependency policy

Implement from cited protocol standards. Do not copy or translate SIPSorcery source.
Any imported implementation, test or asset needs a pinned file-level license review,
original notices and provenance entry before inclusion. Our MIT license does not
replace an upstream license. Do not claim a formal clean-room process or independent
security audit.

Prefer .NET framework APIs. New runtime dependencies require maintainer review of
purpose, provenance, license, the entire transitive/native graph and packaged assets.
Account for build/test tools separately; `PrivateAssets` alone does not prove runtime
independence. See [provenance](docs/provenance.md) and [build tooling](docs/build-tooling.md).

## Pull requests

Keep changes focused, explain wire/API compatibility, and add tests that would catch
the reported failure: malformed inputs, cancellation/teardown, resource limits and
independent interoperability where relevant. Use bounded queues, allocations, retries
and destinations. Preserve fail-closed authentication and trim/AOT diagnostics.

Update the relevant scope document and changelog for user-visible changes. Versions
remain `0.x`, and API changes may occur during development. Use a descriptive commit
message, preferably Conventional Commits. Do not include credentials, private SDP,
provider responses or media recordings. Contributions are submitted under MIT;
retain notices for any separately licensed permitted material.

Maintainers apply [AGENTS.md](AGENTS.md) when working in the shared organization
workspace. External contributors can use a fork and pull request.
