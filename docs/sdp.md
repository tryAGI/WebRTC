# Initial SDP and Opus/data negotiation

`SdpSessionDescription.Parse` constructs immutable bounded signaling models without
opening sockets or resolving names. `SdpNegotiation.CreateOpusOffer` and
`CreateOpusAnswer` implement an initial one-audio/one-data subset.
`ValidateOpusAnswer` produces ICE and DTLS roles, remote credentials/fingerprint,
remote candidates, the selected Opus payload type, effective audio direction,
accepted outgoing header extensions and a bounded data-message limit.

## Supported subset

- Static `v=0` sessions with mandatory origin/name/time, MID per media section,
  SHA-256 fingerprints and media/session inheritance of transport fields.
- One optional BUNDLE group; all accepted sections must have identical effective
  ICE credentials, fingerprint and setup. Multiple active sections must be bundled.
  A single unbundled section is accepted. This does not implement transport-field
  inheritance from a BUNDLE-tag section or multiple independent transports.
- One Opus audio section with dynamic payload type 96–127, RTP clock 48 kHz,
  two-channel SDP mapping and RTCP mux.
  The selected payload type is preserved; the answer cannot introduce codecs,
  media sections, MIDs, payload types or header extensions that were not offered.
- Audio sendrecv/sendonly/recvonly/inactive directions. MID-extension directions
  are validated; outgoing extensions come from the accepted answer. The authored
  answer selects a sendrecv MID extension if offered; other directions are omitted.
- One `UDP/DTLS/SCTP webrtc-datachannel` application section with `sctp-port` and
  `max-message-size`. The limit is the smaller local/remote limit, capped at 1 MiB;
  absent remote size defaults to 65536, zero means unbounded before our cap.
  Advertise the actual configured receive capacity; parsing does not configure SCTP.
- Offer actpass and answer active/passive; fixed roles are checked for compatibility.
  Full ICE offerer is controlling; an answerer facing remote ICE-lite is controlling.
  Local ICE-lite is explicitly unsupported.
- Unsupported offered sections keep their index/MID but are rejected with port zero.
  Rejected sections cannot be revived by an answer. Bundle-only requires port zero
  and group membership; an answer cannot use bundle-only.
- Candidates parse host/srflx/prflx/relay syntax. Only literal component-1 UDP
  addresses resolve to `IceCandidate`; hostnames and other protocols remain syntax.
  Existing ICE transport supports host and owned UDP relay paths to resolved remote destinations.
  `SdpLocalTransport` can include up to eight gathered srflx mappings of the host base
  or same-family relay allocations with their own mapped base,
  explicit gathering-complete markers and trickle capability without performing I/O.

The caller must authenticate signaling, apply destination policy and own transport
startup/teardown. SDP parsing itself does not gather candidates, create an identity,
negotiate keys, send media or implement a peer connection.

## Bounds and rejection

Limit | Maximum
---|---
UTF-8 bytes and input characters | 65536
Lines / characters per line | 1024 / 2048
Media sections | 8
Total attributes per video / other section | 512 / 128
Unknown attributes per section | 128
Candidates per media section | 64
Formats / header extensions / sources per section | 128 / 32 / 64

Duplicate critical transport/security fields, invalid fingerprints/roles, control
characters, malformed framing, plaintext `k=`/SDES `a=crypto`, invalid candidate
ports/priorities and inconsistent negotiations fail before transport side effects.
Unknown attributes are bounded and ignored; unsupported critical selected media
still fails negotiation. Session connection fields are syntax, never route authority.

## Evidence and remaining work

Protocol cases cover inheritance, read-only models, hostile bounds, invalid answers,
roles/directions, rejected video and a deterministic mutated-input corpus. Local
integration exchanges encoded synthetic Opus and control data over one authenticated
ICE/DTLS/SRTP/SCTP BUNDLE. A pinned full Pion WebRTC peer provides independent
SDP offer/answer in both signaling roles and both DTLS answer roles; its public track
API receives and echoes the encoded packet with unchanged source timestamp and payload.
Whole-library NativeAOT smoke executes SDP-driven media/data exchange for all SRTP
profiles. Tests use loopback candidates and no provider endpoints.

This is not full RFC 9429 JSEP conformance or an application-ready peer connection.
General signaling state/rollback, renegotiation, ICE restart, multiple-interface gathering,
name resolution, multiple accepted video sections/general MID routing,
Full feedback capabilities beyond PLI, codec decoding and timed media delivery remain. Synthetic packet
transport is not audible playback or measured provider/Apple Watch voice latency.
The [initial owned peer](peer-connection.md) now supplies the initial signaling
lifecycle and bounded Opus routing above these helpers. Explicit H264/VP8 codec
selection and video routing now extend that initial peer; see [video negotiation](video-peer.md).

## RTCP negotiation

Media-level AVPF feedback is retained in bounded immutable models. The initial
peer offers `nack pli` for supported video codecs and `rtcp-rsize` for audio/video.
An answer can remove capabilities but cannot add a feedback value, widen it to an
unoffered selected format or add reduced-size support. Specific and wildcard
feedback are compared by their actual selected-format coverage. Only negotiated
PLI is actionable; unknown bounded feedback values do not enable a feature.
A stable random local CNAME is shared by bundled audio/video sources and their
control reports. See [automatic RTCP](rtcp.md) for source and scheduling policy.
