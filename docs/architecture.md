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

New public models must not expose SIPSorcery types. Define transport-facing codec,
connection-state and encoded-frame types when implementing the respective milestone.

## Delivery gates

1. Protocol foundation: bounded STUN/RTP parsing, independent integrity vectors,
   malformed-input tests and full-library NativeAOT rooting. Implemented initially.
2. Local ICE/STUN transport and minimal mDNS/DNS: consent, role conflict, retransmit
   deadlines, candidate policy and bounded name resolution. Then TURN UDP/TCP.
3. DTLS handshake and SRTP/SRTCP: fingerprint binding, certificate policy, key
   derivation, authentication before delivery, replay and rollover tests. Do not
   treat fresh protocol glue as a reason to invent cryptographic primitives.
4. SDP negotiation and RTP/RTCP encoded-media transport, interoperability with a
   separately pinned local browser/Pion peer, including loss/reordering/cancellation.
5. SCTP/DCEP data channels with bounded reassembly and flow control. Then migrate
   DId and Advantage separately after consumer regressions and acceptance evidence.

No milestone is complete merely because its API compiles. No production migration
or MIT relicensing of restricted source is part of this initial foundation.

## Standards

- RFC 8489: STUN, https://www.rfc-editor.org/rfc/rfc8489
- RFC 3550: RTP/RTCP, https://www.rfc-editor.org/rfc/rfc3550
- RFC 8445 / RFC 7675: ICE / consent freshness
- RFC 8656: TURN
- RFC 8825 / RFC 8826 / RFC 8827: WebRTC protocol and security architecture
- RFC 5764 / RFC 3711 / RFC 7714: DTLS-SRTP / SRTP / AES-GCM profiles
- RFC 8831 / RFC 8832 / RFC 8833: data channels / DCEP / DTLS usage

Implement the wire contracts from these standards. Copying RFC code components or
test assets is a separate import with separate attribution requirements.
