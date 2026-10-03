# UDP ICE transport scope

`IceUdpTransport` binds a caller-selected IP/port and performs regular nomination
for one RTP component from a host base to resolved remote endpoints. It supports
controlling/controlled roles, authenticated peer-reflexive learning, trickle input,
role-conflict convergence and bounded exponential STUN retransmission. Remote relay
candidates are admitted as UDP destinations: the remote peer owns its TURN allocation.
Our local TURN allocation/routing is not implemented. The independent peer is forced
to relay-only policy, with loopback-only TURN permissions and an active allocation;
it exchanges encoded Opus and data in both offer/answer and DTLS roles, including trickle.

`RemoteCandidateFilter` applies to explicit destinations and authenticated
peer-reflexive learning before admission, role changes or a response. Predicates
must be fast, pure and nonblocking; they are admission policy, not dynamic revocation.
Dispose a transport when revoking an already selected destination.

Credentials are per-generation ASCII ICE characters with RFC 8445 length bounds;
generated passwords use cryptographic randomness. Requests verify the expected
username, unique attributes, HMAC and final fingerprint before changing state.
Up to 16 authenticated requests arriving before remote credentials are applied may
be buffered for at most two seconds. They receive no response or nomination before
signaling, and the full remote username and role are checked again when applied.
This avoids dropping a first check during answer processing and waiting for its RTO.
Responses require a live transaction ID, expected source and valid authentication.
Unselected-source datagrams are dropped, but selected-source data remains untrusted
and must be authenticated by DTLS/SRTP above this class.

Default pacing is 50 ms, initial retransmission timeout 500 ms, at most seven sends
per transaction, a 15-second nomination deadline and at most 64 candidate pairs.
No aggressive nomination is used. Checks can start immediately after credentials
and one candidate are known; this API does not depend on gathering completion.
Unreachable-candidate/ICMP errors do not abort the whole checklist; bounded retry
and consent deadlines decide liveness while other pairs continue checking.

After nomination, outbound consent uses fresh, non-retransmitted transactions every
5 seconds with 20% jitter and expires after 30 seconds (RFC 7675). Only authenticated
responses to outbound checks renew consent; incoming checks and application packets
do not. Expiry closes the socket and data queue. Shorter explicit intervals/deadlines
are available for local loss tests; no expiry may exceed 30 seconds.

The receive queue and datagram size are bounded; overflow drops oldest data. The
upper media layer must not queue stale audio. Diagnostics expose nomination timing,
check RTT, retransmissions and drops without passwords. These are transport boundaries,
not end-to-end voice or playback metrics.

## Explicit same-socket STUN gathering

`GatherServerReflexiveCandidateAsync(resolvedServer, options, cancellationToken)`
sends an unauthenticated STUN Binding request on the same socket used for ICE/media.
It returns immutable mapping/base metadata with srflx priority and a typed candidate
attribute writer. No server is contacted implicitly and no DNS is performed.
Callers select permitted server endpoints. A mapping is not peer authentication;
ICE credentials, DTLS fingerprint binding and SRTP still gate media.

Default STUN timing is initial RTO 500 ms, seven requests, exponential intervals,
last wait 16 times the initial RTO and a 40-second caller deadline. Options bound
these values; the local tests use a 100 ms RTO for their known network. RTT estimation
and caching across transactions remain. One pending transaction per server and eight
overall bound admission. Exact response source, random 96-bit transaction ID,
framing, mapping family/address/port and unique XOR address are checked. Plain STUN
permits an absent fingerprint; a present fingerprint must verify, and callers can
require it. Unknown optional attributes are ignored; unknown required attributes or
valid error responses fail the transaction. No automatic redirect/auth retry occurs.

Gather cancellation/deadline removes its admission without closing the socket;
transport disposal wakes and joins pending operations. Diagnostics report active
transactions, sent requests/retries, correlated rejections and successful mappings.
Local tests exercise IPv4/IPv6, loss/retry, incorrect source/ID/CRC, absent required
fingerprint, duplicate address, hostile mappings, error/attribute handling, admission,
disposal and subsequent ICE after cancellation. Pion's local TURN service independently
answers Binding; native smoke executes gathering on the public peer's owned socket.

This is not yet a complete RFC 8445 implementation: multi-interface gathering, mDNS,
local TURN, restart, extended checklist policies and unknown-required-attribute error
responses remain. See [the acceptance matrix](acceptance.md).
