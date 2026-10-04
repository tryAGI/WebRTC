# Build and test dependency provenance

The consumer assembly uses the .NET shared framework. Build and independent-peer
supply chains are broader and are not claimed to have undergone a security audit.

## Versioning

MinVer **8.0.0** is a private build dependency, following the organization's existing
SDK convention. Upstream: [adamralph/minver](https://github.com/adamralph/minver),
Apache-2.0, source commit `8bbc4fb0230bedfa6f7678749c7b5fe977d240eb`.
The NuGet manifest declares a development dependency and no NuGet dependencies.
Its MSBuild target executes MinVer during build/pack to derive version properties
from Git; the runtime source does not call it. Its original license remains in the
tooling package. No MinVer source or binary is imported into our runtime package.

Review a freshly restored `project.assets.json`, package contents and runtime usage
when updating this tool; `PrivateAssets="all"` alone is not that review. Publication
also refuses any dependency, native asset or embedded tool in the produced nupkg.

Docker test contexts deliberately omit `.git`. Their build-only assemblies use
`MinVerSkip=true` and synthetic `Version=0.1.0-test`. These images are never packed
or published. Published assemblies are built from a full Git checkout with MinVer.

## Other tools

.NET SDK/MSBuild/linker/NativeAOT come from Microsoft. Python verification uses only
its standard library. GitHub Actions are pinned to source commits. Their permissions
are read-only except the tag release job's repository contents write permission.
NuGet's existing `NUGET_KEY` is used only in the publishing job.

Independent Pion Go modules and Chromium/container tooling are test-only, with their
pinned graphs, licenses and notices documented in [provenance](provenance.md),
[Pion notices](../tests/interop/Pion/THIRD_PARTY_NOTICES.txt) and
[Chromium test scope](../tests/interop/Chromium/README.md).
