# UDP ICE transport scope

`IceUdpTransport` binds a caller-selected IP/port and performs regular nomination
for one RTP component using resolved host/server-reflexive endpoints. It supports
controlling/controlled roles, authenticated peer-reflexive learning, trickle input,
role-conflict convergence and bounded exponential STUN retransmission. It does not
gather server-reflexive candidates or route relays. Relay candidates are explicitly
rejected until TURN allocation/routing is implemented.

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

This is not yet a complete RFC 8445 implementation: multi-interface gathering, mDNS,
TURN, restart, extended checklist policies and unknown-required-attribute error
responses remain. See [the acceptance matrix](acceptance.md).
