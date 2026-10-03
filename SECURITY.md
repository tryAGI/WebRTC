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
derived credential key; long-term credential derivation is not implemented. FINGERPRINT
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

SCTP validates CRC32C, verification tags, framing and bounded state over DTLS.
Reliable DCEP channels have aggregate byte/count admission and strict UTF-8/control
validation; unsupported partial reliability fails explicitly. See
[SCTP/data-channel scope](docs/data-channels.md) for limits and missing features.
CRC32C is not authentication. Observe channel-owner failures and close the eventual
peer connection; per-stream reset/rejection remains unimplemented.

A full peer connection, partial reliability/stream reset, NAT traversal and consumer
acceptance remain unimplemented. Consumers must not migrate on DTLS/SRTP interoperability alone.
