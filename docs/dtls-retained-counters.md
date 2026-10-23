# Retained DTLS handshake counters

`DtlsSrtpTransport.GetHandshakeCounters()` and `PeerEstablishmentEvidence.DtlsHandshake`
expose version 1, fixed-size handshake observations independently of opt-in packet tracing.
Snapshots freeze after establishment, failure or disposal and survive trace overflow,
expiry, trace disposal and peer teardown. Live fields can advance independently.

Receive/framing counts are not authentication evidence. Only VerifiedFinishedMessages
counts a cryptographically verified Finished. CompletedRecordSends counts successful
local socket operations, not remote receipt. Pending fields describe bounded reassembly
state before failure cleanup. No packet bytes, endpoints, SSRCs or sequence numbers are retained.
Counters do not change authentication, replay rules, retransmission timers or fallback behavior.

Regression coverage includes success, silent timeout, missing CertificateVerify,
application traffic after freezing, trace wrap/expiry and disposal. Pion cases combine
client certificate fragment loss, client key/signature reordering, encrypted Finished
before CCS and first server Finished loss across all three SRTP profiles. A separate
Pion negative control withholds every CertificateVerify and must fail closed.
These cases require Pion-enabled CI; owned-peer tests alone do not prove independent interoperability.

2026-10-23 local validation: Release integration build, zero warnings/errors; all 54
DTLS-filtered network cases passed. Build execution 8.96 seconds, queue-inclusive
131.764 seconds (build SLO breach); test execution including admission 52.699 seconds.
Provider/device reliability remains separate acceptance work.
