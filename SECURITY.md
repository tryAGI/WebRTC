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

No untrusted packet may trigger unbounded allocation, reassembly, retry loops or network
destinations. Add explicit limits when each stateful transport component is introduced.
SDP-provided ICE servers, DNS answers, candidates and datagrams remain untrusted input.

Consumers must not use this initial foundation to establish secure WebRTC sessions.
