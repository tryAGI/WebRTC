# Initial video peer negotiation

`PeerConnectionOptions.VideoCodecs` is empty by default. Applications explicitly
provide the capabilities of their external encoder/decoder, in preference order.
There is no video codec engine, external NuGet package or native runtime dependency.

```csharp
var codecs = new VideoCodecCapability[]
{
    new() { Codec = VideoCodec.H264, PayloadType = 102,
        H264ProfileLevelId = "42e01f", H264PacketizationMode = 1 },
    new() { Codec = VideoCodec.Vp8, PayloadType = 96,
        Vp8MaximumMacroblocks = 3600, Vp8MaximumFrameRate = 30 },
};
// Use these numbers only when the application's codec supports them.
var options = new PeerConnectionOptions
{
    LocalEndPoint = resolvedInterface,
    VideoCodecs = codecs,
    VideoDirection = SdpDirection.ReceiveOnly,
};
await using var peer = new PeerConnection(options);
// Exchange authenticated signaling and connect as described in docs/peer-connection.md.
await foreach (var frame in peer.ReceiveVideoAsync(cancellationToken))
{
    // H264 is AnnexB; VP8 is descriptor-free encoded data, not decoded pixels.
    // Validate/use with the application's codec and its negotiated limits.
}
```

## Negotiated format

The initial BUNDLE accepts one Opus section, one video section and optional SCTP.
Offers list up to eight distinct dynamic video PTs, excluding the Opus PT111.
Answers retain one compatible offered PT and its MID/m-line position, selected by
local preference. Unsupported/rejected video sections remain rejected. Both DTLS
roles and SDP directions use the accepted answer. Inconsistent transport material,
changed PT/MID, incompatible profiles/modes, upgraded symmetric H264 levels,
unauthorized asymmetry, invalid header extensions and audio/video PT collisions
are rejected before transport starts. General JSEP and multiple accepted video
sections/PT collision routing are not implemented.

H264 modes0/1 negotiate explicit profile/level, RFC6184 table5 equivalent
sub-profiles, Level1b and bilateral level asymmetry. Missing remote profile/level
uses RFC6184 Baseline Level1; missing packetization mode is0. External capabilities
must be explicit. Intra/high and other additional constraint combinations match
exactly rather than inferring a broader decoder. `max-recv-level`, interleaved mode2,
source-level/out-of-band level parameter sets are separate unsupported gates.

`VideoFormat` reports local and remote receive profile/level bounds, the selected
packetization mode and remote VP8 max-fs/max-fr. Missing remote VP8 limits remain
unknown (null); local VP8 capabilities always advertise explicit values. These are
receive capabilities, not proof that an application encoder can produce every such
stream. Outgoing encoding must obey both its own capabilities and remote limits.

Remote H264 parameter sets have bounded Base64/NAL/profile/level header validation.
They remain untrusted encoded syntax; this does not validate/decode a complete SPS
or PPS. They belong to the remote declaring sender and are discarded on a level
downgrade. `RemoteH264ParameterSets` exposes them explicitly for the application
codec; the transport never silently prefixes them onto every frame.

## Source ownership and lifetime

Only SRTP-authenticated/replay-checked RTP reaches video routing. Negotiated PT,
MID when present, and announced remote SSRCs constrain admission. Unannounced
sources may be learned only after valid authenticated payload/header admission and
within the lifetime source limit. Local sources, announced sources of other media,
and SSRCs already learned as audio are excluded. Learned video sources cannot be
reclassified as audio by changing PT/MID. Rejected sections cannot create sources.

Defaults: four lifetime sources, a shared4MiB partial-payload budget, 2MiB expanded
frame maximum, two queued complete frames (at most4MiB), and250ms absolute frame
age. `PeerVideoOptions` explicitly bounds changes; queued capacity times maximum
frame size cannot exceed16MiB. All sources share the partial-byte budget, including
payload descriptors/fragment overhead. Completed-frame queues drop oldest frames.
A25ms maintenance timer expires partial frames during silence and joins the peer
lifetime; audio-only peers do not start this timer. Disposal releases incomplete
buffers; already queued output follows normal channel-drain semantics.

`SendVideoRtpAsync` sends one externally packetized fragment, with90kHz timestamp
and marker. It bounds the negotiated payload/MTU, validates its transport framing,
sets the owned PT/SSRC/MID and serializes sends with unique SRTP sequence indexes.
The caller owns encoding, packetization, pacing/congestion control and access-unit
boundaries. Automatic compound reports and negotiated PLI now share the owned
RTCP worker. Bootstrap, silence expiry and queued-reference loss request a bounded
refresh; explicit decoder failure uses `RequestVideoKeyFrame`. Admitted remote PLI
becomes a bounded encoder event, with encoding owned by the application. See
[RTCP control](rtcp.md). Automatic packetization, actual decoding, congestion control
and retransmission remain requirements for complete consumer delivery.

## Evidence

Authored positive and negative protocol cases exercise codec selection, profile
variants, level downgrade/asymmetry, defaults, malformed parameters and hostile
answers. Network tests use real local UDP/DTLS/SRTP, both codecs/all SRTP profiles,
both DTLS roles, both media directions, bounded queues, silent expiry, global byte
budgets and source/MID isolation. Full independent Pion WebRTC tracks echo audio
and video in both SDP/DTLS roles. Owned peer video also traverses UDP/TCP/TLS TURN
allocations; whole-library-rooted NativeAOT executes those paths.

Synthetic payloads establish transport delivery, not decoding, rendering, actual
DId/Simli sessions, browser/NAT coverage, physical Watch playback or latency gains.
Those remain completion gates in [acceptance](acceptance.md).
