# Scope and milestones

## Supported runtime

Target `net10.0`, consumable by .NET 10 and later. Use C# 14, nullable analysis,
NativeAOT-compatible APIs and spans for bounded datagrams. No old TFM shims, SIP
stack, reflection serializers or bundled codec engine are in the initial scope.
Transport accepts encoded media; applications choose their own codecs.

## Consumer requirements

- DId.Realtime: remote SDP offer/local answer, ICE/STUN/TURN credentials, trickled
  candidates, encoded H264/VP8 and audio frames, inbound data channels.
- Advantage CodexRealtimePeer: local offer/remote answer, encoded Opus audio in both
  directions, outbound data channel, connection lifecycle, SRTP authentication and
  replay protection. Preserve the consumer's bounded queues and packet timing evidence.
- Advantage's separate LiveKit room bridge also has signaling and room semantics;
  replacing SIPSorcery does not replace that implementation.
- Simli: local offer/remote answer over generated WebSocket signaling, supplied ICE
  servers, encoded H264/VP8/audio reception; PCM input remains on its WebSocket path.

New public models must not expose SIPSorcery types. Define transport-facing codec,
connection-state and encoded-frame types when implementing the respective milestone.

## Delivery gates

1. Protocol foundation: bounded STUN/RTP parsing, independent integrity vectors,
   malformed-input tests and full-library NativeAOT rooting. Implemented initially.
2. Local UDP ICE/STUN now has nomination, consent, role conflict, retransmit and
   bounded peer-reflexive/trickle handling; independent Pion validation is present.
   Complete candidate gathering and minimal mDNS/DNS with
   deadlines, candidate policy and bounded name resolution. Then TURN UDP/TCP.
3. DTLS handshake and SRTP/SRTCP: fingerprint binding, certificate policy, key
   derivation, authentication before delivery, replay and rollover tests. SRTP/SRTCP
   directional key contexts and independent profile tests are implemented. Bounded
   DTLS 1.2/EMS fingerprint-bound client/server handshakes and negotiated media now
   interoperate with pinned Pion; browser and complete peer acceptance remain.
   Do not treat fresh protocol glue as a reason to invent cryptographic primitives.
4. SDP negotiation and RTP/RTCP encoded-media transport, interoperability with a
   separately pinned local browser/Pion peer, including loss/reordering/cancellation.
   Bounded initial Opus/data SDP negotiation now drives one BUNDLE transport with
   independent full Pion WebRTC offer/answer and encoded Opus/control exchange in
   both DTLS roles. Initial owned peer lifecycle, accepted Opus MID/PT/source
   routing and bounded RTCP framing are now implemented; automatic RTCP feedback,
   video, general negotiation and browser
   coverage remain. See [SDP subset](sdp.md).
   See [owned initial peer](peer-connection.md) for readiness and queue semantics.
5. Ordered/unordered SCTP/DCEP channels with bounded reassembly, flow control,
   reliable/limited-retransmission/timed policies, directional reset/channel close/reuse
   and independent Pion validation
   now exist. Complete stream
   interleaving and path behavior, then migrate
   DId, Advantage and Simli separately after consumer regressions and acceptance evidence.

No milestone is complete merely because its API compiles. No production migration
or MIT relicensing of restricted source is part of this initial foundation.

## Standards

- RFC 8489: STUN, https://www.rfc-editor.org/rfc/rfc8489
- RFC 3550: RTP/RTCP, https://www.rfc-editor.org/rfc/rfc3550
- RFC 8445 / RFC 7675: ICE / consent freshness
- RFC 8656: TURN
- RFC 8825 / RFC 8826 / RFC 8827: WebRTC protocol and security architecture
- RFC 5764 / RFC 3711 / RFC 7714 / RFC 6188: DTLS-SRTP / SRTP / AES-GCM / AES-256 KDF
- RFC 6347 / RFC 5246 / RFC 7627 / RFC 8422 / RFC 5289 / RFC 5705: DTLS 1.2 / TLS / EMS / ECC / GCM / exporters
- RFC 9260 / RFC 8261 / RFC 3758 / RFC 5061 / RFC 6525: SCTP / SCTP over DTLS / partial reliability / extension negotiation / stream reset
- RFC 8831 / RFC 8832 / RFC 8833: data channels / DCEP / DTLS usage
- RFC 8866 / RFC 3264 / RFC 9143 / RFC 9429 / RFC 8841 / RFC 8285:
  SDP / offer-answer / BUNDLE / JSEP / SCTP SDP / RTP header extensions

Implement the wire contracts from these standards. Copying RFC code components or
test assets is a separate import with separate attribution requirements.
