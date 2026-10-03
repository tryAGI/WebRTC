# DTLS 1.2 and negotiated SRTP

`DtlsSrtpTransport` consumes one nominated `IceUdpTransport` receive stream.
Do not run a second ICE datagram reader while it is active. It owns its handshake,
record keys and directional SRTP contexts; the caller owns ICE and `DtlsIdentity`.
Dispose DTLS before ICE, and keep the identity alive through the handshake.

## Authentication and algorithms

Both peers require the exact SHA-256 leaf-certificate fingerprint obtained through
trusted signaling. A fingerprint supplied by the datagram sender is not trusted.
Identity generation uses a fresh platform ECDSA P-256 key and self-signed certificate;
private keys are never exported or saved. WebRTC fingerprint authentication does not
use a CA, hostname or online chain lookup.

The handshake supports DTLS 1.2, ECDHE P-256, AES-128-GCM records, SHA-256 PRF,
and mandatory Extended Master Secret. Local identities use ECDSA/SHA-256; client
mode can also verify an RSA/SHA-256 server with a 2048–4096-bit key. RSA handshake
interoperability has not yet been independently tested. Unsupported negotiations
fail closed. Certificate signatures, CertificateVerify and Finished are verified
before application or media delivery.

The `use_srtp` extension selects AES-GCM-128, AES-GCM-256 or AES-CM/HMAC-SHA1-80.
RFC 5764 exporter material is split into client/server keys and salts according to
DTLS role, with separate send/receive contexts. No master or exported raw key is
exposed publicly. Master-secret bytes and ephemeral ECDH handles are cleared after
successful establishment; disposal releases record and SRTP keys.

## Reliability and resource bounds

- Maximum UDP datagram: configurable 256–1200 bytes; default 1200.
- Handshake body: 16 KiB; at most eight messages ahead of the expected sequence and
  32 KiB aggregate reassembly bodies, plus equally bounded byte-presence maps.
- Conflicting fragment overlap and malformed trailing fragments are rejected.
- At most eight encrypted records can wait for key installation/ChangeCipherSpec.
- Certificate chain: at most eight certificates; at most 32 distinct extensions.
- Handshake deadline: default 15 seconds, configurable 100 ms–60 seconds.
- Initial retransmission timeout: default one second, doubles to 60 seconds.
  Tests explicitly use 100 ms. Retransmission refragments messages with new record
  sequence numbers. The server acknowledges authenticated identical Finished
  retransmissions for four minutes, limited to one response per 100 ms.
- AES-GCM DTLS records use a 64-entry replay window and a conservative per-direction
  budget of 2^24 encryptions/authentication attempts. Exhaustion requires a fresh
  session; automatic renegotiation/rekey is not implemented. Invalid tags never
  deliver plaintext or advance the replay window.
- Application and authenticated media queues each retain at most 128 datagrams,
  dropping oldest entries under backpressure; diagnostics report drops.

A nominated authenticated ICE path has already proved peer reachability. Server
mode therefore skips HelloVerifyRequest by default, saving a cookie round trip.
`RequireCookie = true` enables the bound cookie exchange; client mode accepts
servers that require it. This is not permission to run the transport on an
unvalidated public UDP listener. Loopback measurements are not evidence of a Watch
or provider latency improvement.

## Media and application datagrams

`SendRtpAsync` / `SendRtcpAsync` protect encoded packets; the corresponding receive
stream emits authenticated RTP or RTCP. RTP payload types 64–95 are refused for
RTCP mux compatibility. Packet sizes must fit the configured MTU plus cryptographic
overhead. RTP assembly, codec negotiation, feedback and congestion control remain.

DTLS application datagrams are exposed as a foundation for SCTP. They are not
WebRTC data channels. DCEP, stream ordering, reliability and flow control remain.

## Validation

Local UDP tests exchange application, RTP and RTCP packets in both directions for
all profiles. Negative cases include wrong fingerprints, modified signatures,
record tampering/replay, cancellation, timeouts and malformed fragment flooding.
Loss, handshake-message reordering, encrypted Finished arriving before CCS, cookie
exchange and certificate fragmentation are exercised through a UDP proxy.

The isolated Pion DTLS v3.1.9 peer independently authenticates both roles, derives
RFC 5764 material and decrypts/reprotects RTP/SRTCP for all profiles. Its explicit
fingerprint callback remains required even though self-signed certificate CA
verification is disabled. No key logging is enabled. NativeAOT smoke executes
fragmented handshakes and negotiated SRTP for all profiles with the whole library
rooted and unsuppressed trim diagnostics.

Standards: [DTLS 1.2](https://www.rfc-editor.org/rfc/rfc6347),
[TLS 1.2](https://www.rfc-editor.org/rfc/rfc5246),
[EMS](https://www.rfc-editor.org/rfc/rfc7627),
[ECC](https://www.rfc-editor.org/rfc/rfc8422),
[AES-GCM cipher suites](https://www.rfc-editor.org/rfc/rfc5289),
[DTLS-SRTP](https://www.rfc-editor.org/rfc/rfc5764) and
[exporters](https://www.rfc-editor.org/rfc/rfc5705).
