# tryAGI.WebRTC

An independent MIT-licensed WebRTC implementation being developed for .NET 10 and later.
The initial library provides bounded STUN and RTP datagram parsing, STUN integrity
and fingerprint verification, and bounded STUN message writing. UDP ICE connectivity
implements regular nomination, role conflicts, trickle/peer-reflexive candidates,
retransmission and consent expiry from a host base to resolved remote candidates,
including remote TURN relays. Explicit STUN Binding gathers srflx mappings on the
same owned socket; cancellation preserves that socket for subsequent ICE.
An owned UDP relay allocation path over explicit UDP/TCP/TLS adds long-term SHA256/legacy authentication,
permissions, Send/Data and ChannelData, automatic renewal and bounded deletion.
Owned allocations now join a bounded local-path-aware ICE checklist, with explicit
relay-only policy, permission readiness and selected-path lifetime enforcement.
Directional SRTP/SRTCP contexts implement AES-CM/HMAC-SHA1-80 and AES-GCM
128/256, with authenticated replay and bounded per-source state. DTLS 1.2 adds
mutual SHA-256 fingerprint authentication, ECDHE/EMS and SRTP key negotiation,
with independent Pion interoperability in client and server roles. Bounded SCTP and
ordered/unordered DCEP channels add large text/binary message delivery with reliable,
limited-retransmission or lifetime-limited policies, negotiated stream reset and
per-channel closure/reuse.
Bounded SDP parsing and initial Opus/data-channel offer/answer negotiation now
drive encrypted audio and control messages over one BUNDLE transport, including
interoperability with a complete independent Pion peer.

An initial `PeerConnection` now owns the resolved-host Opus/data session, routes
authenticated audio by negotiated payload/MID/source, exposes bounded receive
queues and preserves RTP sequence, timestamp and SSRC metadata. Secure media readiness
is separate from SCTP/data readiness.

`VideoFrameAssembler` now provides bounded H264 mode 0/1 and VP8 encoded-frame
reassembly from already authenticated RTP, with pinned source/PT/MID, loss/reorder
handling, sequence rollover and explicit memory/age limits. See [video scope](docs/video-frames.md).

**Status: transport in development.** Initial video SDP and peer routing,
automatic compound SR/RR/CNAME and bounded negotiated PLI are implemented.
Explicit STUN/TURN URI gathering now has bounded A/AAAA resolution, required
resolved-address admission and original-host TLS identity; see [server URIs](docs/ice-server-uris.md).
Multiple interfaces, mDNS/SRV discovery, ICE restart, general SDP/JSEP, broader feedback
and real consumer playback remain. See [RTCP control](docs/rtcp.md).
This library does not yet replace SIPSorcery in DId or Advantage.
No package is automatically published.

The runtime library has no third-party NuGet or native dependencies. It uses the .NET
shared framework for cryptography. SDK tooling, including the Microsoft linker and
NativeAOT compiler, is a separate build dependency.

The implementation is authored against protocol standards. No SIPSorcery or Pion
source has been imported. See [source provenance](docs/provenance.md),
[architecture and milestones](docs/architecture.md), [completion gates](docs/acceptance.md),
[UDP ICE scope](docs/ice-transport.md), [DTLS scope](docs/dtls.md), [SCTP/data-channel scope](docs/data-channels.md), [SRTP/SRTCP scope](docs/srtp.md) and [security scope](SECURITY.md).
See [SDP/Opus negotiation scope](docs/sdp.md) for supported signaling and its limits.
See [owned TURN scope](docs/turn-transport.md) for authentication, lifecycle and integration gaps.
See [initial peer API](docs/peer-connection.md) for lifecycle, media timing and ownership.

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

An independent pinned Pion ICE/DTLS/SRTP/SCTP/DCEP/full WebRTC peer runs in an isolated local container:

```sh
docker build -f tests/interop/Pion/Dockerfile -t tryagi-webrtc-interop .
docker run --rm tryagi-webrtc-interop
```

This is real local UDP/TCP/TLS relay interoperability, not proof of complete media or provider E2E.

An independently authored Chromium test now checks initial browser offer/answer and
both DTLS roles, data-channel policies/reset, two-way Opus decoding with measured
authored tones and silence, protected audio loss/burst recovery, measured RTP pacing
and a delayed-start scenario, fragmented VP8 and H264 rendering, actual protected
packet loss, admitted PLI, a newly encoded recovery key frame and subsequent delta
rendering. It runs in an internal Docker network with disposable profiles:

```sh
./tests/interop/Chromium/run.sh
```

See [browser test scope and tool provenance](tests/interop/Chromium/README.md).
Other H264 profiles/mode 0, general browser renegotiation and consumer/device acceptance remain.

For a targeted local diagnosis, the network runner accepts an explicit literal
`--case-filter "case name fragment"` alongside `--pion-uri` when needed. It prints
the selected subset and rejects an empty match. Default local/container/CI runs
execute every applicable case; a selected subset is not full-suite evidence.

Video requires explicit external codec capabilities in `PeerConnectionOptions.VideoCodecs`.
`ReceiveVideoAsync` yields bounded H264 AnnexB/VP8 frames; `SendVideoRtpAsync`
accepts an externally packetized fragment with its 90kHz timestamp/marker.
See [video negotiation](docs/video-peer.md) for source routing, lifetime bounds and limits.
