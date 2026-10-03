# tryAGI.WebRTC

An independent MIT-licensed WebRTC implementation being developed for .NET 10 and later.
The initial library provides bounded STUN and RTP datagram parsing, STUN integrity
and fingerprint verification, and bounded STUN message writing. UDP ICE connectivity
implements regular nomination, role conflicts, trickle/peer-reflexive candidates,
retransmission and consent expiry for resolved non-relay candidates.
Directional SRTP/SRTCP contexts implement AES-CM/HMAC-SHA1-80 and AES-GCM
128/256, with authenticated replay and bounded per-source state. DTLS 1.2 adds
mutual SHA-256 fingerprint authentication, ECDHE/EMS and SRTP key negotiation,
with independent Pion interoperability in client and server roles. Bounded SCTP and
ordered/unordered DCEP channels add large text/binary message delivery with reliable,
limited-retransmission or lifetime-limited policies.

**Status: transport in development.** ICE gathering/TURN, stream reset,
SDP negotiation and a usable peer connection are not implemented. This library does
not yet replace SIPSorcery in DId or Advantage. No package is automatically published.

The runtime library has no third-party NuGet or native dependencies. It uses the .NET
shared framework for cryptography. SDK tooling, including the Microsoft linker and
NativeAOT compiler, is a separate build dependency.

The implementation is authored against protocol standards. No SIPSorcery or Pion
source has been imported. See [source provenance](docs/provenance.md),
[architecture and milestones](docs/architecture.md), [completion gates](docs/acceptance.md),
[UDP ICE scope](docs/ice-transport.md), [DTLS scope](docs/dtls.md), [SCTP/data-channel scope](docs/data-channels.md), [SRTP/SRTCP scope](docs/srtp.md) and [security scope](SECURITY.md).

## Build and validate

```sh
dotnet build WebRTC.slnx -c Release
dotnet run --project src/tests/WebRTC.ProtocolTests -c Release
dotnet run --project src/tests/WebRTC.IntegrationTests -c Release
dotnet publish src/tests/WebRTC.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -o artifacts/aot
./artifacts/aot/WebRTC.AotSmoke
```

The console test runner needs no test-framework packages and exits nonzero on failure.
All tests run locally without credentials or provider endpoints.

An independent pinned Pion ICE/DTLS/SRTP/SCTP/DCEP peer runs in an isolated local container:

```sh
docker build -f tests/interop/Pion/Dockerfile -t tryagi-webrtc-interop .
docker run --rm tryagi-webrtc-interop
```

This is real UDP interoperability, not proof of complete media or provider E2E.
