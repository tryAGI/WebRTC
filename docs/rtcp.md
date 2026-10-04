# RTCP primitives and automatic peer control

`RtcpPackets`, `RtpReceptionTracker`, `RtcpClock` and
`RtcpTransmissionSchedule` are authored .NET 10 runtime primitives. They add no
NuGet or native dependency. `PeerConnection` owns automatic compound reports and
negotiated H264/VP8 picture-loss feedback. Encoding, decoding, congestion control,
retransmission and provider/browser playback remain application/acceptance work.

## Semantic codec

`RtcpPackets.TryParse` consumes at most 1200 bytes and 32 RTCP packets. It owns
its returned data and exposes SR/RR reception blocks, SDES CNAME, BYE and PLI.
Cumulative loss is signed 24-bit; PLI is PT 206/FMT 1 with exactly two SSRCs and
no FCI. Unsupported feedback remains `RtcpOpaquePacket` and is never an
interpreted request. Other SDES items are bounded/skipped; their wire bytes remain
in the caller's original datagram. Unknown packet semantics must be ignored by
applications unless they implement and negotiate that capability separately.

SDES chunks require END and zero alignment padding, valid nonempty UTF-8 CNAME,
no duplicate CNAME/source in a chunk list, and no contradictory CNAME across the
datagram. PRIV's prefix must fit its item. BYE has bounded distinct sources and
an optional UTF-8 reason with zero alignment padding. Strict rejection of malformed
metadata is an application policy; it is not a source-identity assertion.

`compound` means a leading SR/RR and a CNAME for each interpreted report sender.
It is a structural classification, not proof of authentication, source authorization,
participant count or reduced-size negotiation. Feedback may have a different
sender from an aggregated report (RFC 8108); this flag does not authorize it.
The caller must authenticate SRTCP before interpreting fields, validate sources
against signaling/authorized RTP, require negotiated feedback and gate reduced-size
receiving on the binding answer. Regular reports must remain full compounds even
when reduced-size feedback is negotiated (RFC 5506).

`Encode` supports SR, RR, SDES CNAME and PLI, enforcing the same packet/datagram
bounds and signed-loss range. It deliberately cannot reconstruct unknown SDES items
from the CNAME-only view or encode opaque/BYE semantics. Callers own source identity,
compound ordering, timing and negotiated capabilities. Parsed packet lists, report
lists and SDES/BYE collections are read-only snapshots, independent of input bytes.

## Reception statistics and clocks

Create one `RtpReceptionTracker` per already authorized source, with its negotiated
RTP clock rate (48000 for Opus, 90000 for these video codecs). The caller supplies
monotonic elapsed arrival times. Two adjacent sequence numbers validate initial or
restart state; discontinuous probation is replaced. Forward jumps below 3000 are
accepted; larger discontinuities need a new adjacent pair. The last 99 sequence
positions permit bounded misordering. Duplicates count as received and can produce
negative cumulative loss. This is report accounting, not replay protection, a jitter
buffer or a media loss-recovery mechanism. SRTP authentication/replay checks precede it.

`CreateReport` advances interval expected/received counters, computes fractional
loss, clamps signed cumulative loss, extends 16-bit sequence rollover, estimates
interarrival jitter with the RFC 3550 prose EWMA, and optionally carries an admitted
sender report's compact LSR plus elapsed DLSR in 16.16 seconds. Restart resets the
statistical epoch and jitter; a rejected candidate never changes accepted counters.
Arrival-time validation includes rejected candidate observations. The tracker does
not keep unbounded per-packet history or source dictionaries.

`RtcpClock.ToNtpTimestamp` produces the unsigned seconds/fraction wire value from
UTC. The seconds word wraps at the NTP era boundary; parsing does not infer an era.
`Compact` selects its middle 32 bits. RTT still requires matching a recent report
actually sent by the owner; arbitrary received LSR values are not evidence of RTT.

## Endpoint scheduling

`RtcpTransmissionSchedule` receives this endpoint's already allocated RTCP byte
share and estimated datagram size including transport overhead. It uses a randomized
interval, 1/16 size EWMA and explicit point-to-point early-feedback admission from
RFC 3550/4585 prose. It coalesces to one early send between regular transmissions,
postpones a regular slot after an early send, and treats a previously admitted send
that was delayed past the regular deadline as regular. Failed/invalid send records
must not change average size or slot state. Record only successful transmissions.

Set `pointToPoint` only when signaling guarantees two participants. A unicast socket,
one observed CNAME or multiple SSRCs does not by itself prove this topology. A caller
that discovers conflicting topology must invoke `DisableEarlyFeedback`; this is
irreversible and idempotent. Unknown topology uses regular sending only, with the
AVPF initial minimum interval. This is a single-endpoint allocation helper, not a
multicast membership estimator, full timer-reconsideration algorithm, token bucket
or congestion controller. Its compensation/randomization permits short-term bursts;
a peer owner still needs a hard on-wire byte budget and feedback/source bounds.
One scheduler can coalesce local BUNDLE SSRCs; do not allocate the full session share
independently to every source. Clock/random callbacks must not reenter the scheduler.

## Evidence and remaining integration

Protocol tests use an independent Python struct wire fixture and assert fields,
malformed SDES/PRIV/BYE, conflicting CNAME, report-count/signed-loss/byte bounds,
truncation, unknown feedback, sequence/timestamp rollover, duplication, restart,
jitter, NTP era and deterministic early/regular/topology scheduling.
Local network tests send those fields through ICE UDP and all three authenticated
SRTCP profiles, with replay rejection. The authored Pion harness generates separate
SR/RR/CNAME/PLI/BYE packets using its pinned public RTCP API, independently decodes
and re-encodes our signed report fixture, and exchanges controls through
fingerprint-bound DTLS/SRTCP in both roles/all profiles. NativeAOT roots the whole
library and executes the primitive and local encrypted network gates.

Next integrate negotiated `rtcp-fb`/`rtcp-rsize`, stable per-peer CNAME, admitted
sources, periodic SR/RR, a shared bandwidth budget, loss/expiry/decoder requests and
a bounded encoder key-frame request queue. Then validate independent complete-peer
feedback and hostile/cancellation behavior. No browser decoder, provider or Watch
latency result follows from this primitive/transport evidence.

Primary standards: [RFC 3550](https://www.rfc-editor.org/rfc/rfc3550.html),
[RFC 4585](https://www.rfc-editor.org/rfc/rfc4585.html),
[RFC 5506](https://www.rfc-editor.org/rfc/rfc5506.html),
[RFC 8108](https://www.rfc-editor.org/rfc/rfc8108.html).

## Automatic PeerConnection owner

Offers advertise `rtcp-rsize` on audio/video and `nack pli` for supported video
formats. Answers retain only offered capabilities for the selected codec; wildcard
feedback is compared by the formats it covers. The binding answer governs both
roles. Audio/video share one random per-peer CNAME, stable through candidate
updates. A CNAME is a grouping hint, not an authenticated participant identity.

One bounded worker sends full SR/RR plus SDES compounds, even with reduced-size
feedback negotiated. Reports rotate admitted reception sources within the control
MTU. SR packet/octet counters advance after successful RTP writes; NTP/RTP mapping,
loss intervals and DLSR are built after bandwidth admission, immediately before
the transport write. RTT requires a matching SR actually emitted within one minute
and a nonnegative result within that window. Reception accounting follows accepted
routing/payload admission, so it is not a measurement of network-only loss.

`PeerRtcpOptions` bounds the endpoint control rate, reception sources and PLI
interval/attempts. The endpoint share is the smaller of the configured cap and
2.5% of session bandwidth. A token bucket permits one MTU-sized initial burst and
accounts for a conservative128-byte transport overhead estimate; this is not exact
wire-byte measurement or a congestion controller. Audio RTP never waits for RTCP
credit. `PointToPoint` defaults false; set it only with a signaling guarantee.
Two distinct admitted CNAME groups irreversibly disable early feedback.

Valid authenticated video bootstrap, frame expiry and lost queued references mark
one refresh episode. A complete syntactically identified key frame clears it;
this does not prove successful decoding. `RequestVideoKeyFrame(remoteSource)` lets
an application report decoder failure for an announced/admitted source. Requests
are coalesced and throttled, default500ms with at most three automatic attempts
per episode. A key/reset cannot bypass the last-send cooldown. A dropped reference
superseded by a queued key for the same source needs no new refresh.

`ReceiveVideoKeyFrameRequestsAsync` yields a bounded eight-event queue for admitted
remote PLI targeting this peer's negotiated local video sender. The application
owns the encoder response. Foreign/audio/local sender collisions, unnegotiated
PLI, contradictory CNAME and invalid compounds are rejected before report or
feedback side effects. Unsupported opaque feedback has no actionable effect.
Reduced-size receiving is checked against the negotiated media; an unclassified
reporter requires all active RTP media to allow it. Control-only reporter and
CNAME maps are bounded separately from RTP reception state.

`SendRtcpAsync` is an advanced SR/RR/CNAME API with an eight-request queue, immediate
failure when full, owned input snapshot and cancellation/teardown completion. It
shares the byte budget; callers own report fields and regular-report timing.
PLI must use the scheduled key-frame API and cannot bypass retry/rate policy.
This alpha API no longer accepts arbitrary opaque feedback or BYE. Automatic BYE,
NACK/retransmission, TWCC/REMB, general multiparty membership estimation and full
RTCP conformance remain separate work.

Local tests cover compound clocks/counters/reception/RTT in both DTLS roles/all
SRTP profiles, PLI bootstrap/retries/cooldown/key cancellation/silence expiry,
whole-compound rejection, topology fallback, bounded raw sends/cancellation and
audio isolation. The pinned full Pion peer independently parses reports/CNAME and
exchanges PLI in both SDP/DTLS roles for H264 and VP8. Whole-library-rooted native
execution includes the owned controller; this is synthetic local transport evidence,
not decoded media, real providers, real NAT or measured physical Watch delivery.
