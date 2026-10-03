# Encoded video frame reassembly

`VideoFrameAssembler` consumes **already authenticated, replay-checked RTP** from
`DtlsSrtpTransport.ReceiveMediaDatagramsAsync` (`Kind == Rtp`). It does not perform
SRTP authentication, SDP negotiation, source learning, decoding or rendering.
The application must authorize one remote SSRC, dynamic payload type, codec and
optional MID extension before creating an owner. A conflicting explicit MID is
rejected; an absent MID is allowed because SSRC/PT are pinned. Create independent
owners only for application-authorized sources and bound that owner inventory.

## Wire formats and output

Newly authored from [RFC 6184 sections 5–7](https://datatracker.ietf.org/doc/html/rfc6184)
and [RFC 7741 sections 4.1–4.4](https://www.rfc-editor.org/rfc/rfc7741).
No RFC code components, upstream implementation or example has been imported.

- H264 packetization mode 0: single NAL units (types 1–23).
- H264 mode 1: also STAP-A and FU-A, including empty FU payloads and ignored reserved
  FU-header bits. Fragments must retain NAL identity and have one matching start/end.
  Corrupt NAL flags, nested aggregation, invalid lengths, unfinished and conflicting
  fragments are dropped. Mode 2/DON/MTAP/STAP-B/FU-B are unsupported.
- H264 output is one access unit, with a **four-byte Annex B prefix before every NAL**.
  An IDR NAL marks `IsKeyFrame`; this metadata is not decoder acceptance evidence.
- VP8 removes all payload descriptors and concatenates the encoded bitstream.
  Short/long PictureID, TL0/TID/key-index fields are bounded and PictureIDs must agree
  where present. Reserved fields are ignored. Partition starts/order, initial S/PID=0,
  a final marker and complete packet continuity are required. Key-frame metadata uses
  the bitstream header, with bounded header/signature validation; decoding is external.

Frames retain their original 90 kHz timestamp, SSRC and first/last 16-bit sequence
numbers. Packet sequence ordering spans rollover. A complete frame is returned
immediately, without an extra playout delay; this is not a decoder/jitter buffer.
Late packets cannot resurrect delivered or recently discarded frames. Completion
of newer media discards incomplete older frames; applications must request a
refresh/key frame when their decoder has lost reference state.

## H264 boundary and loss policy

H264 has no RTP access-unit-start flag: a FU start only starts one NAL. Receiving
an IDR suffix does not prove that earlier SPS/PPS/slices in that AU survived.
The initial AU is conservatively held/discarded until a preceding authenticated
marker establishes the next AU's sequence boundary. If that preceding marker is
lost, the receiver waits through the next marker and resynchronizes at the following
AU. Missing packets never produce a supposedly complete AU. This policy can discard
an initial IDR; automatic negotiated PLI/NACK, decoder refresh and SDP video/peer
integration remain separate completion gates. Applications cannot infer general
H264 decoder readiness from this transport-only API.

## Resource and lifetime bounds

Defaults per owner: four in-flight frames, 1024 packets/frame, 2 MiB expanded output
per frame, 4 MiB charged buffered bytes, 4096-byte RTP packets and 250 ms age.
Charged bytes are the larger of retained payload size and reconstructed output size;
STAP expansion and descriptor/empty-FU overhead cannot bypass this budget. Packet and
frame limits separately bound dictionary/object overhead. Recently retired timestamps
and marker history each have 32 entries. No caller input buffers are retained.

Call `Expire()` periodically during silence; expiration also runs before each push.
Age uses monotonic `TimeProvider` timestamps and is not extended by duplicates or
new fragments. Matching duplicates are counted; conflicting duplicates poison the AU.
A forward jump of at least 8192 but less than 32768 packets drops old reassembly and
reestablishes codec boundaries. Very old/ambiguous sequence input is rejected.
Use `Reset()` only after an application-validated new stream generation/restart;
it discards retained media and sequence history. `Dispose()` releases all buffers.
Methods serialize access to one owner. Diagnostics expose retained frames/packets,
charged bytes, completed/dropped frames and rejected/duplicate packet counts.

## Evidence and remaining scope

Protocol cases cover independently authored payload layouts, malformed nested/short
NALs, FU identity, reserved bits, VP8 descriptor/partition identity, MID/PT/SSRC,
missing/late/duplicate packets, sequence/timestamp rollover, expanded-byte/packet/frame
budgets, monotonic age, restart/disposal and a deterministic hostile-input corpus.
Local ICE/DTLS/SRTP integration injects **encrypted** packet reordering/loss/late delivery
without reusing sender nonces. All supported SRTP profiles are exercised.
The pinned local Pion public H264/VP8 payloaders independently packetize our synthetic
bytes, then Pion decrypts and reencrypts them over actual fingerprint-bound DTLS/SRTP
connections in both roles. These synthetic bytes prove transport reconstruction,
not visual/codec correctness. No new module or license import is added.
Whole-library-rooted NativeAOT runs the bounded protocol cases and actual video
DTLS/SRTP through owned UDP/TCP/TLS relay allocations for every SRTP profile.

Full video SDP/PeerConnection routing, RTCP feedback/report scheduling, decoder
refresh behavior, browser/provider videos and DId/Simli migration remain incomplete.
Real provider and physical Watch acceptance stay outside default no-cost CI/smoke.
