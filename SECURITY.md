# Security scope

The project has not received an independent security audit. Parser and cryptographic
verification tests do not establish complete WebRTC transport security.

Current parsers validate datagram boundaries before exposing zero-copy spans. They do
not open sockets or resolve names. A successful RTP parse says nothing about packet
authentication. Future transport code must authenticate packets before delivering media.
The caller must keep backing buffers unchanged while a parsed view or attribute
enumerator is in use; read-only spans do not make the underlying storage immutable.

The assembly is public-signed with the repository's public strong-name key for stable
assembly identity. This is not a publisher-authenticity signature or a security boundary.
No private signing key is committed.

STUN MESSAGE-INTEGRITY uses the HMAC-SHA1 wire algorithm required for existing ICE
interoperability. Hashing is performed by .NET cryptography, and integrity comparisons
use CryptographicOperations.FixedTimeEquals. The caller must supply the correctly
derived credential key. The owned TURN allocation implements bounded ASCII long-term
credential derivation and negotiated SHA256/legacy integrity; see [TURN scope](docs/turn-transport.md). FINGERPRINT
is a CRC for protocol identification and corruption detection, not authentication.

SRTP/SRTCP contexts authenticate before returning decrypted packets, enforce replay
windows and refuse sender index reuse. They require fresh directional key generations;
standalone contexts do not authenticate peers or negotiate keys. See [SRTP/SRTCP scope](docs/srtp.md).
The independent peer and synthetic vectors establish profile interoperability, not an
independent security audit. `DtlsSrtpTransport` authenticates both peer fingerprints,
verifies handshake signatures/Finished and derives directional SRTP material.
Signaled fingerprints must come from trusted signaling. DTLS supports only a bounded
DTLS 1.2/EMS/P-256/AES-GCM subset; unsupported negotiations fail closed.
See [DTLS scope and key limits](docs/dtls.md).

No untrusted packet may trigger unbounded allocation, reassembly, retry loops or network
destinations. Add explicit limits when each stateful transport component is introduced.
SDP-provided ICE servers, DNS answers, candidates and datagrams remain untrusted input.
The initial peer owns all transports and closes them on establishment cancellation
or transport failure. Only authenticated/replay-checked SRTP reaches Opus routing;
wrong MID/payload/direction, conflicting/local sources and excess source admission
are rejected. Known RTP extension framing is checked before routing. RTCP framing
checks do not constitute semantic feedback/SDES validation. Bounded drop-oldest media
queues report losses and add no intentional jitter delay. See [peer scope](docs/peer-connection.md).
The SDP parser bounds UTF-8 bytes, lines, media sections, attributes, candidates,
codecs and extensions. It performs no network/DNS work. Initial Opus/data negotiation
validates accepted media, BUNDLE credentials, fingerprint, DTLS roles and extension
directions before the caller starts transport. Trusted signaling must authenticate
those credentials and fingerprints. Parsing a hostname does not authorize resolving
it; candidate destination policy remains the transport caller's responsibility.
SDES/plaintext keys are rejected. Unsupported active media fails negotiation.
See [SDP limits](docs/sdp.md); these checks are not a complete JSEP/security audit.

SCTP validates CRC32C, verification tags, framing and bounded state over DTLS.
DCEP channels have aggregate byte/count admission and strict UTF-8/control
validation. PR-SCTP must be negotiated; FORWARD-TSN advances and stream entries are
bounded and validated before receive-state changes. Abandonment releases payload
storage but retains bounded metadata until acknowledgment. See
[SCTP/data-channel scope](docs/data-channels.md) for limits and missing features.
CRC32C is not authentication. Observe channel-owner failures and close the eventual
peer connection. Stream reset is negotiated and validates parameter framing, unique
stream IDs, request sequence and bounded TSN barriers before changing state. IDs
remain reserved until both directions finish; invalid DCEP still terminates its owner.

UDP relay integration binds authenticated ICE transactions, early checks, nomination,
consent and selected ciphertext to the exact local path and remote endpoint. TURN IP
permissions are routing admission, not peer authentication. A failed selected relay
closes its owner rather than moving authentication to another path. Relay-only excludes
host connectivity; SDP related mapped addresses can still disclose address metadata.
See [ICE path scope](docs/ice-transport.md).

General negotiation, complete NAT traversal and real consumer acceptance remain
unimplemented. Consumers must not migrate on DTLS/SRTP interoperability alone.

TURN TCP/TLS stream framing and write admission have fixed bounds. An interrupted
write closes its connection because the remote side might have a partial frame.
No authentication state is reused through reconnect or fallback. TLS uses platform
`SslStream`, TLS 1.2/1.3, strict target-name/chain/time/server-purpose validation and
no validation callback. Custom DER roots replace system trust only when explicitly
supplied; they are snapshotted before connection. Revocation defaults to Online and
may contact certificate revocation services; private isolated fixtures explicitly
choose NoCheck. AIA issuer downloads are disabled, so servers must supply their
intermediate chain. This is transport interoperability, not an independent security audit.
