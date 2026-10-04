# Test-only toolchain. No browser, native codec or package enters the runtime library.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot@sha256:2ef30f119199e148cb35fd954ca61eddcf02f2996059f782899d318451ff4967
WORKDIR /workspace
COPY Directory.Build.props ./
COPY src/libs/WebRTC/ src/libs/WebRTC/
COPY src/public.snk src/public.snk
COPY README.md LICENSE ./
COPY tests/interop/Chromium/ tests/interop/Chromium/
RUN dotnet build tests/interop/Chromium/BrowserPeer.csproj -c Release -p:MinVerSkip=true -p:Version=0.1.0-test
ENTRYPOINT ["dotnet", "/workspace/tests/interop/Chromium/bin/Release/net10.0/BrowserPeer.dll"]
