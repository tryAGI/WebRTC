# tryAGI.WebRTC

An independent MIT-licensed WebRTC implementation being developed for .NET 10 and later.
The initial library provides bounded STUN and RTP datagram parsing, STUN integrity
and fingerprint verification, and a STUN Binding request header writer.

**Status: protocol foundation.** ICE connectivity, TURN, DTLS/SRTP, SCTP/data channels,
SDP negotiation and a usable peer connection are not implemented. This library does
not yet replace SIPSorcery in DId or Advantage. No package is automatically published.

The runtime library has no third-party NuGet or native dependencies. It uses the .NET
shared framework for cryptography. SDK tooling, including the Microsoft linker and
NativeAOT compiler, is a separate build dependency.

The implementation is authored against protocol standards. No SIPSorcery or Pion
source has been imported. See [source provenance](docs/provenance.md),
[architecture and milestones](docs/architecture.md) and [security scope](SECURITY.md).

## Build and validate

```sh
dotnet build WebRTC.slnx -c Release
dotnet run --project src/tests/WebRTC.ProtocolTests -c Release
dotnet publish src/tests/WebRTC.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -o artifacts/aot
./artifacts/aot/WebRTC.AotSmoke
```

The console test runner needs no test-framework packages and exits nonzero on failure.
All tests run locally without credentials or provider endpoints.
