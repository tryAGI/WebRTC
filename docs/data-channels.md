# SCTP and DCEP data channels

The current runtime implements one bounded SCTP association over an authenticated,
nominated DTLS connection, plus ordered and unordered DCEP channels with reliable,
limited-retransmission and timed reliability.
This is progress toward the consumer transport, not complete WebRTC support.
Interleaving, path-MTU probing, general SDP and actual provider/browser acceptance
remain required before migration. The [initial peer owner](peer-connection.md)
already negotiates the bounded Opus/data-channel SDP subset and owns these layers.

## Lifetime and readers

`SctpAssociation` consumes the DTLS application-datagram receive stream; do not run
another application reader on that DTLS instance. SRTP media remains independent.
`DataChannelAssociation` consumes the serialized SCTP data/reset event stream; do not
run a second SCTP reader. Raw consumers needing reset notifications use
`ReceiveEventsAsync`; `ReceiveMessagesAsync` consumes and filters those notifications.
Disposal cancels blocked operations and releases the owned reader, leaving the
lower transport alive. Dispose channels, then SCTP, DTLS, ICE and certificate owners.
Observe each layer's `Completion` task when using the lower-level APIs;
`PeerConnection` owns their readers and propagates failures. SCTP `CloseAsync`
drains outgoing data and performs the
SHUTDOWN/ACK/COMPLETE exchange; abortive disposal is not graceful shutdown.
Already acknowledged buffered messages remain readable after graceful shutdown.
Ready authenticated input is processed before timer output. Once a terminal
shutdown control is accepted, obsolete SACK/reset work is discarded; a required
final SHUTDOWN-COMPLETE is still sent. A failed send after DTLS closure is accepted
as graceful completion only after verifying the peer's SHUTDOWN-COMPLETE.
Send cancellation can precede DTLS completion publication. The SCTP owner joins
that shutdown and drains its bounded authenticated tail before deciding whether
the terminal exchange succeeded. DTLS closure alone remains an association failure.

```csharp
// dtls is already nominated and mutually fingerprint authenticated.
await using var sctp = new SctpAssociation(dtls, SctpRole.Initiator);
await sctp.ConnectAsync(cancellationToken);
await using var channels = new DataChannelAssociation(sctp);
var events = await channels.OpenChannelAsync("oai-events", cancellationToken: cancellationToken);
await events.SendTextAsync(json, cancellationToken);
await foreach (var message in events.ReceiveMessagesAsync(cancellationToken))
{
    if (message.Kind == DataChannelMessageKind.Text)
        HandleEvent(message.GetText());
}
```

SCTP initiator/responder is explicit and independent of DTLS role. DCEP stream parity
always follows DTLS role: client opens even IDs, server opens odd IDs. Both SCTP
initiators are supported. Incoming channels are exposed by `AcceptChannelsAsync`.
Local channel creation waits for DCEP ACK; optimistic early user-data transmission
has not yet been implemented. Canceled opening releases an unadmitted ID immediately;
after OPEN admission, it initiates reset or retires the ID if reset was not negotiated.

## Implemented wire behavior

- CRC32C, strict packet/chunk/parameter bounds, verification tags and negotiated ports.
  CRC is corruption detection; DTLS supplies authentication and privacy.
- INIT/INIT-ACK, an association-bound HMAC cookie, COOKIE-ECHO/ACK, retransmission,
  duplicate handling and simultaneous INIT. Restarts use a new association/DTLS instance;
  transparent SCTP restart and multihoming are not implemented.
- DATA B/E/U/I framing, fragmentation, TSN serial arithmetic/rollover, per-stream
  ordered SSNs, unordered delivery, selective acknowledgments and deduplication.
- Bounded send admission, advertised receive-window credit, zero-window probing,
  congestion windows, slow start/congestion avoidance, fast recovery and timeout
  retransmission. Gap-acknowledged data is retained until cumulative acknowledgment
  so receiver reneging can cause retransmission. RTO sampling follows Karn's rule;
  default initial/minimum RTO is one second, tests explicitly select 100 ms.
- Incoming HEARTBEAT acknowledgment and graceful association shutdown. Periodic
  SCTP heartbeats and PLPMTUD/path-change congestion reset remain; ICE consent and
  DTLS closure currently drive transport failure detection.
- DCEP OPEN/ACK on ordered reliable PPID 50, strict UTF-8 label/protocol decoding,
  all six channel types, text/binary and empty PPIDs 51/53/56/57.
  Empty payloads ignore the single placeholder byte on receipt. Obsolete partial
  string/binary PPIDs are not accepted.
- Canonical one-byte DCEP ACK is sent. Exactly `02 00 00 00` is also accepted because
  pinned Pion datachannel v1.6.3 emits it. Other trailing forms are rejected. This
  compatibility exception does not relax DTLS authentication, PPID or stream checks.

PR-SCTP is advertised through both RFC 3758 Forward-TSN-Supported and RFC 5061
Supported Extensions (chunk type 192). A peer without the capability can use reliable
channels; partially reliable sends/OPENs fail explicitly. Invalid DCEP terminates
its channel owner and signals failure, rather than pretending to reset a stream.
Invalid OPEN rejection isolated to one stream remains a future milestone.

## Partial reliability

Use the typed `OpenChannelAsync(DataChannelParameters, cancellationToken)` overload
with `RetransmissionLimited` or `Timed`. Retransmission limits exclude the original
transmission; zero means send once. Timed parameters are milliseconds and cover the
full uint range. Lifetime starts when the send API is called, including waiting for
local admission. If it expires before admission, no TSN or ordered stream sequence
is consumed. Cancellation is distinct from expiration. Reliable channels normalize
their wire parameter to zero; DCEP OPEN/ACK always remain ordered and reliable.

Once admitted, all fragments of an expired/exhausted message are abandoned together,
including fragments not yet transmitted. Payload credit is released immediately;
bounded TSN/stream metadata remains until the peer acknowledges FORWARD-TSN.
Abandonment is counted once per message and never earns congestion-window credit.
FORWARD-TSN advances only across contiguous abandoned chunks after the actual
cumulative acknowledgment, and retries until acknowledged. `DrainAsync` includes
that acknowledgment. Send completion indicates admission or lifetime expiration,
not successful delivery; consult `AbandonedMessages` for aggregate diagnostics.

Receivers reject duplicate/out-of-range stream entries and excessive TSN/SSN jumps
before state changes. Complete messages stranded by missing earlier sequences are
preserved and delivered; incomplete skipped fragments are released. Ordered skips
continue correctly when the application delivery queue is full. Interleaved I-DATA and arbitrary large receive lookahead are not silently negotiated.

## Stream reset and channel closure

RFC 6525 RE-CONFIG (130) is advertised through Supported Extensions. The subset
originates outgoing SSN reset (13), accepts incoming/outgoing SSN reset (14/13),
and validates responses (16). Association-wide TSN reset and stream growth are
explicitly denied. Selected streams or all streams can reset; TSNs continue across
an SSN reset. Request numbers use initial TSN and serial arithmetic with a 2^31
lifetime bound. One outgoing reset flight freezes new SSN assignment for its streams.
Explicit successful results release it; a reciprocal outgoing request alone does
not prove success. Duplicate requests replay a bounded cached response. Requests
retry with RTO/backoff; an In-progress result retries without counting peer failure.

Incoming resets wait for the sender's TSN barrier and enqueue their notification
only after all earlier selected-stream messages enter the serialized receive queue.
Future DATA for those streams is held until their sequence state resets. A bounded
notification reservation includes pending and queued resets; default/maximum is
128 and configurable minimum is two. Payload delivery credit is independent.

`DataChannel.CloseAsync` stops new sends and waits for both directional resets.
Previously acknowledged messages remain on the old channel object after closure;
new OPEN may reuse the ID only after both resets complete. A send waiting for
admission checks its original channel generation before entering the SCTP queue.
Cancellation stops only the caller's wait, preserving the admitted wire exchange.
A refused reset leaves the ID reserved and preserves earlier buffered messages.
`DataChannel.DisposeAsync` explicitly discards unread application messages, returns
aggregate receive credit and initiates closure; it does not wait for the wire
exchange. Use `CloseAsync` or `Completion` when confirmed closure is required.
A peer without stream reset cannot gracefully close/reuse a single channel: the
close API refuses before changing state, while disposal retires the ID.

## Bounds and admission

Defaults: 128 negotiated streams, 256 KiB messages, 1152-byte SCTP send packets,
1 MiB received payload storage, 2 MiB queued send payload, 128 ready delivery slots,
4096 outstanding/reassembly/gap and retained-message entries and 128 queued control chunks. Metadata,
wire headers and a transient assembled-message copy are additional bounded storage.
Packet size must fit the underlying DTLS application limit; choose a smaller value
when configuring a smaller DTLS MTU. SCTP assumes a stable packet size for this stage.

The DCEP owner additionally limits all channels together to 64 channels, 128 queued
messages and 1 MiB queued application payload, plus one pending message. Labels and
protocols are limited to 1024 UTF-8 bytes each. Limits are validated and configurable
within explicit ceilings. Complete application messages are not discarded to relieve backpressure. Gap-filling DATA can
use the bounded ordering reserve while the delivery queue is full, so a delayed
ordered message cannot deadlock behind a later received message.
When payload storage is exhausted, gap-filling DATA can replace the highest
undelivered fragments acknowledged only through SACK gaps (RFC 9260 section 6.2).
Their gap acknowledgments are revoked; the sender retransmits the retained data.
Cumulatively acknowledged data and messages already released to readers are never
revoked. Receive storage must be at least 1500 bytes and fit one maximum message.
A slow channel can currently backpressure the owner's other channels; fair independent
channel admission/scheduling is deferred. It does not consume the SRTP media reader.

`SendMessageAsync`/channel sends finish when admitted to the bounded send queue, not
when acknowledged; `DrainAsync` waits for SCTP acknowledgment. Keep caller buffers
unchanged until a send completes. Receive credit is returned when the message is
removed from the receive stream; applications own the returned arrays. Neither raw
SCTP nor channels may be used as an unbounded application retention buffer.

After 2^31 transmitted TSNs, establish a fresh association before serial arithmetic
becomes ambiguous. A peer that fails acknowledgment beyond configured retransmission
limits terminates the association. A responsive zero-window peer may remain stalled
until the application reads or cancels. No negotiated feature or valid protection is
silently disabled to make an exchange pass.

## Evidence and remaining gates

Protocol tests check a synthetic packet independently checksummed by Go's standard
Castagnoli CRC implementation, every-byte corruption, truncation, excessive chunks,
DCEP channel types, Unicode/invalid UTF-8 and deterministic hostile input.
Local UDP cases cover 256 KiB bidirectional fragmented messages, TSN rollover,
packet/INIT loss, ordered/unordered delivery, buffer backpressure, malformed input,
canceled/silent peers, DCEP text/binary/empty/multiple streams and cancellation of
blocked sends. PR cases cover both orderings and policies, TSN wrap, whole fragmented
message abandonment, FORWARD-TSN loss/retry, a two-retransmission budget, expiry before
admission, cancellation, maximum lifetime and negotiation refusal. The whole-library
NativeAOT smoke executes zero-window timed abandonment/FORWARD-TSN and all policies
over a 256-byte DTLS MTU with each SRTP profile, plus actual channel closure and ID
reuse. Reset tests cover request/result loss, reciprocal resets, simultaneous close,
request/SSN wrap, all-stream reset, deferred partial abandonment, bounded notification
admission, buffered delivery, canceled waiters, explicit buffer disposal, blocked old
sends and capability refusal.

The independent isolated peer uses pinned Pion SCTP v1.12.0 and datachannel v1.6.3
public APIs. It exercises DTLS/SCTP roles, local/remote DCEP opening, large/empty/text/
binary messages and shutdown. Independent bidirectional PR-SCTP loss cases require
FORWARD-TSN to release the next ordered message; duplicate-stream and excessive-TSN
controls with valid CRC are rejected before a later valid control succeeds. Its
module graph and original MIT notices are test-only. Closure cases exercise both
initiators, simultaneous closure and ID reuse in both DTLS roles. CRC-valid duplicate
and out-of-range reset IDs are rejected before a later valid retry succeeds.
The full independent suite additionally repeats simultaneous closure twenty times
in each DTLS role to exercise terminal SCTP/DTLS ordering.
No Pion implementation is included in the .NET runtime. Tests remain local and key-free.

Standards: [SCTP](https://www.rfc-editor.org/rfc/rfc9260),
[SCTP over DTLS](https://www.rfc-editor.org/rfc/rfc8261),
[WebRTC data channels](https://www.rfc-editor.org/rfc/rfc8831),
[DCEP](https://www.rfc-editor.org/rfc/rfc8832),
[PR-SCTP](https://www.rfc-editor.org/rfc/rfc3758) and
[stream reconfiguration](https://www.rfc-editor.org/rfc/rfc6525).
