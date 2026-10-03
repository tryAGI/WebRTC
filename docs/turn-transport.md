# Owned UDP TURN allocation

`TurnUdpAllocation` owns one UDP socket and one allocation on an explicitly selected,
resolved unicast TURN endpoint. It does not resolve a URI, pick a server or contact
provider endpoints by itself. It is a datagram path prerequisite: it is not yet a local
candidate path in `IceUdpTransport`/`PeerConnection`. Remote relay candidates supported
by the initial peer are a separate capability.

## Authentication and bounds

`AllocateAsync(local, server, credentials, options, cancellationToken)` completes a
bounded Allocate exchange. It pins the expected server and each random 96-bit ID,
checks framing and a present fingerprint, and requires authenticated success. A
fingerprint is not authentication. Transactions inherit explicit STUN RTO/request/
deadline bounds; same-transaction retries preserve packet bytes and socket. The
allocation lifetime also bounds subsequent control requests.

Long-term authentication implements modern full 32-byte MESSAGE-INTEGRITY-SHA256,
SHA256 password-key derivation, PASSWORD-ALGORITHMS/PASSWORD-ALGORITHM echo/selection,
nonce feature checks and USERHASH anonymity. SHA256 is selected when offered, including
when the server also lists MD5. Unknown algorithms with well-framed parameters are
skipped; an unsupported list, missing nonce-required list, changed realm, repeated
nonce or changed negotiated authentication fails. At most two fresh challenge retries
are admitted. No implicit alternate-server redirect or credential-source switch occurs.

`AllowLegacyAuthentication` defaults to true for existing TURN servers without modern
negotiation; set it to false to require SHA256 password keys. Legacy MD5-derived keys
and HMAC-SHA1 are protocol interoperability mechanisms, not general password-storage
or signing choices. Runtime uses platform cryptographic primitives. Strict profiles
accept one integrity attribute, not mixed SHA1/SHA256 envelopes or truncated SHA256.

Credentials and realm currently accept bounded printable ASCII only. Unicode PRECIS
preparation is not implemented; unsupported inputs are rejected instead of prepared
incorrectly. The supplied credential object has a redacted `ToString`. Caller-owned
strings cannot be erased; temporary password bytes and the owner's derived key are
cleared. Do not log credentials or application-generated TURN responses containing them.

## Permissions, channels and lifetime

Before `SendDatagramAsync(peer, bytes)`, explicitly create a permission or bind a
channel. Permissions apply to an IP, while channels bind a full peer endpoint. A
pure, fast `PeerFilter` applies to outbound admission and indicated inbound sources.
Default caps: 32 retained permission addresses, 32 retained channel endpoints, 1200-byte
inner datagrams and a 32-entry receive queue; options admit at most 64 peers, 16384-byte
inner datagrams and 256 queued entries. A full queue drops its oldest entry and counts
it. One caller control operation is admitted at a time; another is rejected immediately.
The single maintenance owner can wait for that operation. A changed accepted lifetime
wakes maintenance; refresh requests run before allocation/permission/channel expiry.

Unbound peers use Send/Data indications. Bound peers use ChannelData. A pending bind
can receive early ChannelData; outbound ChannelData waits for acknowledged binding.
Canceled/failed binds retain their endpoint/number as a tombstone and reuse that same
mapping on explicit retry. Numbers are never reassigned within the allocation, avoiding
old-binding reuse races. Tombstones consume the configured budget and are not renewed.
Both padded and unpadded UDP ChannelData are bounded. Oversized received datagrams
are dropped, including OS MessageSize errors, without stopping the allocation.

Cancellation of an existing caller control operation preserves the owner; late replies
cannot satisfy a new transaction. Allocation creation failure closes its socket. If a
successful allocation supplies unsupported addresses, the owner attempts authenticated
deletion. `DisposeAsync` joins maintenance and active controls, requests a zero lifetime
with a two-second deletion budget, then closes the socket and readers. An authenticated
zero-lifetime response or allocation-mismatch response is required for
`GracefulReleaseAcknowledged`. Remote deletion is best effort; an unreachable server
or a lost initial success can leave server state until its lifetime expires. Refresh
failure/expiry faults `Completion` and readers; it never silently continues an expired
allocation. Recorded server lifetimes are supported from 1 through 86400 seconds.

`Candidate`, mapped/base endpoints and receive source endpoints defensively copy their
address values. `TurnDatagram` is relay-sourced data, not authenticated peer media:
TURN indications and ChannelData have no peer integrity. ICE/DTLS/SRTP must authenticate
inner traffic. Do not deliver raw TURN bytes directly to an audio or data-channel consumer.

## Evidence and remaining gates

Local cases cover IPv4/IPv6, legacy/modern authentication, nonce renewal, dropped
requests, wrong server/ID/key, stripped algorithms, policy/resource bounds, zero-length
ChannelData, cancellation/disposal, automatic renewal, expiry and unsupported-family
cleanup. SHA256 framing/HMAC has an independently generated Python known-answer vector
and byte/key tampering tests. The rooted native executable runs modern authenticated
allocation, ChannelData and deletion against an authored local server.

The pinned independent Pion TURN v5.1.2 public-API service proves real local Allocate,
CreatePermission, Send/Data, ChannelBind/ChannelData, Refresh and acknowledged deletion;
its allocation count returns to zero. Test server addresses/permissions are loopback-only,
credentials random and ephemeral, and its UDP echo peer is local. This is UDP TURN
interoperability, not real NAT traversal or provider/device acceptance.

Local-path-aware ICE checklist integration, TURN TCP/TLS, DNS/mDNS/multiple interfaces,
ICE restart, browser and real NAT/provider/Watch acceptance remain required. No consumer
has migrated as a result of this prerequisite.
