# Initial owned Opus/data peer

`PeerConnection` owns its UDP ICE socket, ephemeral DTLS identity, DTLS/SRTP,
optional SCTP/DCEP association and receive loops. It supports the initial
[SDP subset](sdp.md) with a resolved local interface and resolved non-relay remote
candidates. It does not gather interfaces, resolve names, use STUN/TURN servers,
implement renegotiation or replace the existing consumer adapters yet.

## Signaling and lifetime

```csharp
await using var peer = new PeerConnection(new PeerConnectionOptions
{
    LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0), // Local testing.
});
var offer = peer.CreateOffer();
// Exchange through the application's authenticated signaling.
peer.SetRemoteAnswer(answer);
await peer.ConnectAsync(cancellationToken);
var channel = await peer.OpenDataChannelAsync(
    new("oai-events", "", true, DataChannelReliability.Reliable, 0, 256),
    cancellationToken);
```

An answerer calls `CreateAnswer(remoteOffer)` and sends the result before starting
`ConnectAsync`. Signaling errors leave the previous state intact. One instance has
one initial negotiation and ICE credential generation. Trickle uses
`AddRemoteCandidate` after connection establishment has started; the argument is
the candidate attribute body. An optional candidate predicate applies application
destination policy. Unsupported hostname/relay/other-family candidates in an SDP
are not admitted. A session with no supported candidates can wait for trickle until
its bounded establishment deadline; it never automatically resolves or contacts a server.

The state transitions are New → HaveLocalOffer → Ready → Connecting → Connected
for an offerer, or New → Ready for an answerer, followed by Failed or Closed.
`Completion` reports a transport failure or null for an intentional/graceful close.
Startup cancellation closes the entire owned session and wakes pending operations.
Caller cancellation after successful connection does not close the session.
`DisposeAsync` is idempotent, joins all owned loops and releases the socket/keys.
`CloseAsync` first drains/shuts down negotiated SCTP and then disposes locally;
cancellation still disposes. Remote graceful SCTP shutdown closes the owner cleanly.
Audio-only disposal is local teardown; DTLS close-notify/RTCP BYE generation remains.
DataChannel objects are borrowed from this owner; closing one does not close the peer.
An advertised data-message limit also respects the configured DCEP receive budget.

## Audio and readiness

`MediaReady` completes after peer-authenticated DTLS/SRTP, before a possibly stalled
SCTP handshake. `DataChannelsReady` completes when SCTP/DCEP ownership exists, and
`ConnectAsync` completes when all negotiated transports are ready. An application
may await `MediaReady` and start media while observing the eventual connection task.
It must continue to observe `Completion`, since later SCTP/ICE failures close the owner.
Readiness cannot bypass fingerprint authentication or fresh ICE consent.

`SendOpusAsync(payload, rtpTimestamp, marker)` sends one encoded Opus RTP packet.
The caller owns pacing and the 48 kHz RTP timestamp, including wraparound; input
encoder sample rate does not change that clock. The sender uses a random SSRC and
initial sequence, serializes sends, emits only accepted MID extensions and never
reuses a consumed sequence after encryption/transmission cancellation. Check
`MaximumAudioPayloadBytes` before choosing encoder packet size. An oversized Opus
packet is rejected rather than fragmented into an invalid Opus payload.

`ReceiveAudioAsync` preserves source/sequence/timestamp/marker and copies the
encoded payload out of the transport packet. It performs no decoding, jitter
buffering, reordering, codec PLC or playback. Audio is admitted only after SRTP
authentication/replay checks, negotiated payload/direction and valid known extension
framing. A present MID must match the audio section; other-section/local SSRCs and
excess source generations are rejected. With no MID, the unique accepted audio
payload provides routing. New source admission is bounded (8 by default).

The default audio queue holds 8 packets, drops the oldest on overflow, and reports
every drop. Control queue default is 32 packets. Queue counts are not a bound in
milliseconds; Opus packet duration varies. Diagnostics expose queue drops, routing
rejections, media-ready/connected times and the underlying ICE/DTLS counters.
Receive streams distribute packets among readers; they are not broadcast streams.
Apps must preserve their existing continuity, queue-age and source-clock policies.

## RTCP and evidence

`ReceiveRtcpAsync` exposes authenticated bounded compound/reduced-size RTCP after
length/padding/SR/RR count checks. `SendRtcpAsync` requires valid framing and the
local sender SSRC. These methods do not implement SDES item validation, periodic
SR/RR, feedback dispatch, RTT/jitter statistics or retransmission. They are raw
control transport primitives, not full RTCP conformance.

Local tests cover media/data/control in both DTLS answer roles, signaling recovery,
socket release on failure, cancellation/disposal, audio direction, queue overflow,
authenticated wrong MID/PT/SSRC, source bounds and malformed RTCP. A stalled-SCTP
case proves early authenticated media readiness, including a negotiated two-byte MID.
Independent complete Pion WebRTC
tests use this public owner in all four signaling/DTLS-role combinations and verify
Opus payload, source clock, marker/sequence continuity, data and graceful teardown.
Whole-library NativeAOT executes the owner with all three SRTP profiles.

Real DId/Advantage/Simli provider sessions, physical Watch playback and measured
latency distributions remain separate acceptance gates. This initial peer provides
neither a complete browser/JSEP implementation nor an independent security audit.
