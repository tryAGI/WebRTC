# tryAGI.WebRTC

**WebRTC transport for .NET 10, authored from standards and licensed under MIT.**

[![Publish](https://github.com/tryAGI/WebRTC/actions/workflows/dotnet.yml/badge.svg)](https://github.com/tryAGI/WebRTC/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/vpre/tryAGI.WebRTC.svg)](https://www.nuget.org/packages/tryAGI.WebRTC)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](src/libs/WebRTC/WebRTC.csproj)

Build encrypted audio, video and data transports with explicit network admission,
bounded resource use and a runtime that depends only on the .NET shared framework.
The library owns ICE, DTLS/SRTP and SCTP channel lifetimes; your application owns
signaling, media encoding/decoding and playback.

> **Experimental `0.x`.** APIs and supported negotiation profiles are evolving.
> Independent Pion and Chromium checks pass for the documented scope; real
> DId/Advantage/Simli sessions and physical Apple Watch playback are still acceptance
> work. This is not yet a drop-in replacement for those adapters or a security-audited
> general WebRTC stack. See [completion gates](docs/acceptance.md).

## Install

```sh
dotnet add package tryAGI.WebRTC --prerelease
```

Requires .NET 10 or later. Main builds publish `0.x` development packages after all
validation jobs succeed; `v0.x.y` tags create releases. See
[publication and immutable-source verification](docs/package-publication.md).

## Start a peer

```csharp
using System.Net;
using tryAGI.WebRTC;

await using var peer = new PeerConnection(new PeerConnectionOptions
{
    LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0), // Local testing.
});

string offer = peer.CreateOffer();
// Send offer through your application's authenticated signaling.
// Obtain the remote answer, then:
// peer.SetRemoteAnswer(answer);
// await peer.ConnectAsync(cancellationToken);
```

For a remote connection, choose a reachable local interface and an application
candidate/destination policy. Signal ICE credentials and DTLS fingerprints over an
authenticated channel. No public STUN/TURN server or provider endpoint is contacted
by default. An answerer uses `CreateAnswer(remoteOffer)`.

[Peer lifecycle and API](docs/peer-connection.md) covers gathering/trickle, channels,
encoded media, readiness, cancellation and disposal. `MediaReady` allows authenticated
media to start before data-channel setup finishes; callers still observe `Completion`.

## Supported scope

| Area | Implemented and tested | Current limits |
| --- | --- | --- |
| ICE / STUN | UDP checks, nomination, consent, trickle, same-socket mappings, bounded URI/DNS gathering | Explicit local interface; no mDNS/SRV, remote candidate DNS or ICE restart |
| TURN | Owned relay allocations over UDP, TCP or TLS; authentication, renewal, permissions, relay-only policy | Bounded documented profiles; not proof of every NAT topology |
| DTLS / SRTP | Fingerprint-authenticated DTLS 1.2, directional SRTP/SRTCP, replay and key-use bounds | Explicit cipher/handshake subset; trusted signaling required |
| Audio | Negotiated Opus RTP transport, source/clock metadata, bounded receive queues | Encoded packets; application owns codec, pacing, jitter policy and playback |
| Video | Explicit H264/VP8 capabilities, bounded frame reassembly, RTCP reports and negotiated PLI | Application owns codec/packetization; no general codec or SDP renegotiation |
| Data channels | SCTP/DCEP, ordered/unordered reliable or partial delivery, fragmentation, reset/closure | Documented negotiation/resource limits apply |
| Compatibility | Independent local Pion and Chromium; actual decoded Opus/video and protected loss recovery | Consumer/provider/device E2E remains |
| NativeAOT | Whole-library rooting, publish and executed smoke lane | Linux x64 CI evidence; other native targets need their own acceptance |

See the scope documents for [ICE](docs/ice-transport.md), [server URIs](docs/ice-server-uris.md),
[TURN](docs/turn-transport.md), [DTLS](docs/dtls.md), [SRTP](docs/srtp.md),
[SDP](docs/sdp.md), [data channels](docs/data-channels.md),
[video](docs/video-peer.md) and [RTCP](docs/rtcp.md).

## Validate locally

```sh
dotnet build WebRTC.slnx -c Release
dotnet run --project src/tests/WebRTC.ProtocolTests -c Release --no-build
dotnet run --project src/tests/WebRTC.IntegrationTests -c Release --no-build
python3 -m unittest discover -s scripts -p 'test_*.py'
```

Console runners exit nonzero on failure and need no test-framework package.
Tests use local peers and synthetic inputs without provider keys.

**Independent Pion peer** — local ICE/DTLS/SRTP/SCTP/DCEP and relay checks:

```sh
docker build -f tests/interop/Pion/Dockerfile -t tryagi-webrtc-interop .
docker run --rm tryagi-webrtc-interop
```

**Chromium** — offer/answer and both DTLS roles, data-channel policies/reset,
two-way decoded Opus, RTP pacing, protected packet loss/recovery and H264/VP8 rendering:

```sh
./tests/interop/Chromium/run.sh
```

See [browser evidence and tooling](tests/interop/Chromium/README.md). Docker is required;
these checks run in an isolated network with disposable browser profiles.

**NativeAOT**, on a Linux x64 host with the .NET native toolchain:

```sh
dotnet publish src/tests/WebRTC.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -o artifacts/aot
./artifacts/aot/WebRTC.AotSmoke
```

A build alone is not execution evidence. Targeted runner filters are useful for
local diagnosis; passing a subset does not replace the full validation suite.

## Security and provenance

Runtime source is newly authored against the standards in
[architecture](docs/architecture.md). No SIPSorcery or Pion implementation has been
imported into the runtime. Build and independent test tooling have separate licenses
and dependency graphs; see [source provenance](docs/provenance.md) and
[build tooling](docs/build-tooling.md). MIT licensing does not establish security.

Read [SECURITY.md](SECURITY.md) for trust boundaries and private vulnerability reporting.
Strong naming provides assembly identity, not publisher authentication.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md), [code of conduct](CODE_OF_CONDUCT.md) and
[changelog](CHANGELOG.md). Focused fixes, resource-bound regressions and independent
interoperability reports are welcome.

- [Report a bug or propose a feature](https://github.com/tryAGI/WebRTC/issues/new/choose)
- [Ask a question or discuss a design](https://github.com/tryAGI/WebRTC/discussions)
- [Browse releases](https://github.com/tryAGI/WebRTC/releases)

## License

[MIT](LICENSE) — tryAGI and contributors. Separately licensed test/build tooling
retains its original notices.
