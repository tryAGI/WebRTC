# Completion evidence for DId, Advantage and Simli

The active objective is full usable transport and verified consumer E2E, with particular
attention to Codex voice delivery to Apple Watch. A foundation, a compatible API,
self-to-self exchange or green parser tests cannot satisfy that objective.

## Required gates

| Requirement | Required evidence | Current evidence / gap |
|---|---|---|
| Authenticated ICE and nomination | Independent peer in both roles, role collision, credentials, loss, consent expiry, IPv4/IPv6 | Local network cases include pinned Pion v4.4.5 in both roles and signaling-delayed early checks; full NAT/checklist coverage remains |
| Real NAT traversal | STUN srflx gathering, TURN UDP/TCP/TLS, relay permissions/channel lifetime, mDNS and multiple interfaces | Same-socket bounded STUN mapping, local hostile/loss tests and independent Pion Binding; encrypted Opus/data to relay-only Pion with an actual local TURN allocation in both signaling/DTLS roles, including trickle; owned UDP TURN allocation/permission/channel/refresh/delete independently passes local Pion; owned local relay ICE now passes encrypted Pion Opus/data in both signaling/DTLS roles, relay-to-relay and trickle/late admission; owned TCP/TLS server transports pass local Pion encrypted Opus/data in both signaling/DTLS roles and relay-to-relay; TLS identity-negative, framing and interrupted-write tests are local only; explicit provider URI/A/AAAA gathering now has local IPv4/IPv6 policy/cancellation and UDP/TCP/TLS identity/teardown coverage; real NAT matrix, remote candidate DNS/mDNS, SRV/NAPTR and multiple interfaces remain |
| Safe ICE lifecycle | Trickle, peer-reflexive learning, restart, gathering cancellation, bounded checklist | Trickle/reflexive/bounds and destination policy on discovered sources implemented; canceled/timed-out gathers preserve the socket and subsequent ICE; disposal and admission bounded; restart remains |
| DTLS-SRTP | Independent peer, SHA-256 fingerprint binding, client/server roles, exporter, retransmission and malformed flights | Local DTLS 1.2/EMS client/server handshakes, fingerprint/signature/replay rejection, loss/reordering/fragmentation, negotiated media and pinned Pion DTLS v3.1.9 in both roles; Chromium 151 initial browser connection now passes both DTLS roles with DCEP policies/reset and VP8/H264 loss recovery; full consumer sessions remain |
| SRTP/SRTCP | Positive vectors and negative auth/replay/rollover vectors; live media in both directions | AES-CM/HMAC-80 and AES-GCM 128/256 key contexts implemented; independent ciphertext/decryption and encrypted local UDP tests; DTLS-negotiated bidirectional local media and independent Pion exporter/SRTP exchange now pass; real consumer media remains |
| SDP/BUNDLE/RTCP mux | Actual consumer codecs and SCTP, ICE-lite, rejected sections, MID/SSRC routing | Bounded initial Opus/data offer-answer, consistent bundled transport, RTCP mux, rejected sections, remote ICE-lite role selection and accepted MID-extension directions; owned Opus source routing and full Pion WebRTC in both offer/answer and DTLS roles; general JSEP, local ICE-lite, multiple-media routing and consumer signaling remain |
| Encoded media | Opus timing, H264/VP8 assembly, RTCP feedback, loss/reorder and bounded queues | Owned initial peer preserves synthetic Opus sequence/timestamp/marker/source, bounded queues and accepted MID/PT/source routing with independent full Pion; raw authenticated RTCP framing/transport exists; bounded SR/RR/CNAME/BYE/PLI semantic primitives, loss/jitter/NTP accounting and endpoint scheduling now have independent Pion codec/DTLS exchange, hostile inputs and rooted native execution; automatic peer reports and negotiated bounded PLI now have independent full Pion and rooted native coverage; bounded single authorized-source H264 mode 0/1 and VP8 assembly has independent Pion public payloader plus fingerprint-bound DTLS/SRTP evidence in both roles/all profiles, encrypted loss/reordering/late packet rejection and whole-rooted native relay execution; Initial explicitly configured H264/VP8 video SDP/PeerConnection routing now passes full Pion peers in both SDP/DTLS roles, owned queues, source/MID isolation, silence expiry and UDP/TCP/TLS relays for all SRTP profiles; Chromium 151 now passes two-way 48 kHz Opus decoding with authored ordered tones/final silence, bounded authenticated capture, PCM waveform assertions, silent/wrong-frequency negatives and browser decoded-sample stats in all six role/video cases; each now includes monotonic fixture pacing with independently observed protected RTP clocks, a 120 ms delayed-first-dispatch scenario that must not cause catch-up bursts, and one plus two-adjacent protected audio packet losses with exact browser loss counters/continued tone output (silent container sink, not audible device playback or general jitter/speech quality); Chromium 151 now renders authored fragmented VP8 and constrained-baseline H264 mode 1, sends PLI after an actual dropped protected packet, decodes a newly encoded key frame and continues delta-frame rendering in both SDP/DTLS roles; other H264 profiles/mode 0, pacing/jitter, broader feedback and consumer playback remain |
| SCTP/DCEP | Browser/Pion data channels, ordered/unordered/reliable/limited-retransmission channels, bounded reassembly | Reliable/limited-retransmission/timed ordered and unordered channels, fragmented abandonment, FORWARD-TSN loss/retry, admission expiry and independent Pion bidirectional/hostile-control tests; directional reset, channel close/reuse, lost-result recovery and CRC-valid hostile-reset tests; Chromium now passes six concurrently open ordered/unordered reliability policies, Unicode/empty/fragmented bidirectional messages, owned opening, confirmed closure in both directions and explicit stream-ID reuse in both SDP/DTLS roles; browser PR loss/abandonment, interleaving and broader full peer coverage remain |
| DId | Owned public models/dependency; real agent offer/answer, nonempty media, ready event, teardown | Existing adapter inspected; migration and provider E2E remain |
| Advantage Codex | Preserve pacing, queues, authenticated replay semantics, bootstrap pre-roll and data channel | Existing consumer inspected; migration and real App Server regression remain |
| Apple Watch delivery | Same trace through remote audio, backend/decode/delivery, device arrival/playback; before/after distributions and audible acceptance | No new physical evidence |
| Simli | Owned offer/answer adapter; real face/session, nonempty media, cleanup; preserve WebSocket input/signaling | Existing adapter inspected; migration and provider E2E remain |
| MIT and source ownership | Pinned file-level origin/license checks and notices for every future port/import; audited graph | Newly authored runtime, no imported runtime code |
| .NET 10+, trimming/AOT | Whole-library rooting, executed native transport, supported-platform CI, no weakened diagnostics | Whole-library native ICE/DTLS/SRTP/SCTP/DCEP smoke executes locally; Linux/Windows/macOS CI gates apply to every source commit; final complete transport still requires the same gates |

An inconclusive, skipped, credential-missing or configuration-only test is missing
evidence, never a pass. Record explicit artifacts and independent-peer versions.
Provider/hardware credentials must not appear in logs, fixtures or commits.

## Latency investigation

Signaling waits and audio buffering in the voice consumer are hypotheses for measurement,
not evidence that removing either improves Watch delivery. Preserve authentication,
replay and existing continuity, source-clock and queue-age tests. Keep private consumer
implementation details and provider credentials out of public fixtures and reports.

Record gather start, first candidate, SDP exchange, selected pair, DTLS completion,
channel readiness, first authenticated audio, backend playback submission, device
arrival and audible playback. Compare repeated cold/warm runs, loss/burst conditions
and p50/p95; loopback ICE timing is not provider or physical voice E2E latency.

## Next implementation order

Continue RTCP feedback, video and general peer negotiation over the independently validated owned initial Opus/data peer; add NAT/relay/resolution and consumer adapters as prerequisites
become usable. Then migrate consumers and perform provider/device acceptance.
Revisit order when interoperability reveals a prerequisite.
