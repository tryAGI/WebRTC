# UDP ICE transport scope

`IceUdpTransport` binds a caller-selected IP/port and performs regular nomination
for one RTP component from a host base and up to three owned UDP TURN allocations
to resolved remote endpoints. It supports
controlling/controlled roles, authenticated peer-reflexive learning, trickle input,
role-conflict convergence and bounded exponential STUN retransmission. Remote relay
candidates are admitted as UDP destinations: the remote peer owns its TURN allocation.
Explicit local allocation/routing is also supported. The independent peer can be forced
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
Responses require a live transaction ID, expected source, the same local path and valid authentication.
Unselected-source or unselected-local-path datagrams are dropped, but selected-pair data remains untrusted
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

The receive queue and datagram size are bounded; overflow drops oldest data.
`MaximumDataDatagramSize` is the outgoing application budget (default 1200);
`MaximumReceiveDataDatagramSize` independently limits incoming application data
(default 2048, configurable 64–65507). The selected authenticated ICE path is still
required, and its data remains untrusted until DTLS/SRTP validation. Host and relay
paths apply the same receive bound; TURN allocation storage admits both budgets.
Local UDP and rooted native cases verify the exact default receive boundary,
one-byte oversize rejection, a subsequent valid packet and unchanged outbound limit. The
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
restart, extended checklist policies and unknown-required-attribute error
responses remain. See [the acceptance matrix](acceptance.md).

## Explicit owned relay paths

`GatherRelayCandidateAsync(resolvedServer, credentials, options, token)` binds a separate
socket on the selected interface and attaches an owned `TurnUdpAllocation`. No server
is contacted by default. Successful gathering returns the immutable relay/mapped-base
candidate; its base can differ from the initial host socket. Up to three relay slots
include pending gathers and failed attached allocations. Late gathering pairs the new
path with existing signaled candidates before publishing it. Global pair admission
remains bounded at 64; an over-budget attachment deletes/closes its new owner.

Each pair identifies both the local path and remote endpoint. Transactions, buffered
early requests, nomination, consent responses/requests and data admission retain that
identity. Incoming role checks learn a peer-reflexive candidate only on their own path.
Replies and selected ciphertext use that path; authentication is never silently moved
to another socket. Relay failure removes its checks; failure of the nominated relay
stops the entire transport. Failure of an unselected relay preserves other paths.

A bounded per-path queue serializes permission creation outside the ICE monitor,
sharing TURN control admission with maintenance. Checks wait for acknowledged IP
permissions. Incoming checks before readiness are not sent a reply until a retry;
TURN IP permissions do not replace endpoint/path-bound ICE integrity or DTLS/SRTP.
Denied permissions fail matching pairs; the generation deadline bounds nomination.
TURN's explicit peer policy and retained-address limits still apply. Inner datagram
storage is raised to at least 2048 bytes for ICE checks; TURN's maximum 16384-byte cap
also applies to relay traffic. No channel binding is required for this integration.

`RelayOnly` excludes host checks, host early requests, host consent and host media.
It allows no automatic direct fallback. The initial host socket still exists for
explicit Binding/lifecycle, but a gathered relay is required for ICE connectivity.
This is path policy, not a guarantee that SDP's related mapped address is hidden.
Use destination policy and authenticate signaling separately.

Canceling an unattached gather rolls back only its new allocation. Disposal cancels,
joins gathers/readers/permission workers and disposes every allocation with its bounded
best-effort deletion. Diagnostics expose selected local type/endpoint, active permitted
local paths and pending permission count without passwords.

Authored tests cover IPv4/IPv6, relay-to-relay, late attachment/trickle, permission
readiness/disposal, global pair/three-allocation bounds, selected/unselected expiry,
and correctly authenticated responses/consent/media on the wrong local socket.
Independent Pion uses local relay-only signaling with encrypted Opus/data in both
signaling and DTLS roles; two owned allocations on the same or distinct independent
TURN servers exchange datagrams and return allocation counts to zero. Rooted NativeAOT
executes relay-carried DTLS/SRTP/SCTP for all supported SRTP profiles. These are local
interoperability tests, not browser, real NAT, provider or Watch E2E evidence.

TURN server transport is explicitly selected by `TurnUdpOptions.ServerTransport`;
TCP/TLS carry the same UDP relay allocation, path identity, permissions and ICE state.
Independent local stream relay tests cover both signaling/DTLS roles, encrypted
Opus/data, relay-to-relay, same/different servers and zero remaining allocations.
A selected stream failure stops ICE; an unselected failure preserves a selected host.
TLS uses the explicit policy in [TURN scope](turn-transport.md).
