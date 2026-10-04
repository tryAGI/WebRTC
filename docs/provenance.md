# Source provenance

## Current authored code

All runtime code and committed tests in this initial repository are newly authored.
No source, pseudocode, test implementation or fixture has been copied or translated
from SIPSorcery, Pion or RFC appendices. Protocol constants and field layouts are
implemented from the cited standards. Authentication fixtures are independently
generated from synthetic inputs using Python's standard hmac/hashlib/binascii tools.
SRTP/SRTCP known-answer ciphertexts are generated from newly authored public synthetic
keys and packets by the pinned Pion test peer (`pion-peer --srtp-vectors`), not extracted
from any upstream test fixture or RFC appendix. The generator is our authored harness.
`src/public.snk` is a newly generated RSA public strong-name key; its private key
was discarded. It is used only for assembly identity, not publisher authentication.

This describes code provenance, not a formal clean-room or security-audit claim.
SCTP terminal-shutdown ordering was diagnosed from our own authenticated local
Pion exchanges: a queued SHUTDOWN-COMPLETE could lose to stale SACK output after
DTLS close-notify. The authored fix drains ready input before output, suppresses
post-terminal work and accepts a closed-transport send failure only after a verified
terminal SHUTDOWN-COMPLETE. CI exposed a second publication race: send cancellation
can precede DTLS completion and scheduling of its queued reader. The owner joins
transport shutdown and drains that bounded authenticated tail before deciding.
No upstream algorithm/source or dependency was added.
The initial peer owner, RTP/MID framing/routing and bounded RTCP framing are newly
authored from RFC 8285/3550 and the existing authored transport contracts. The
new independent peer tests invoke our public owner and the existing authored Pion
HTTP harness. No upstream implementation/example/fixture or new dependency is added.

The UDP relay ICE integration is newly authored from RFC 8445/8838 and existing owned
TURN contracts. Each authenticated transaction, early check, consent and selected
media admission retains local path identity. New tests exercise our public API and
the existing Pion HTTP service; no upstream implementation, example, fixture or new
module was imported. This remains authored interoperability evidence, not a formal
clean-room or security audit.

## Independent test peer

STUN gathering and candidate metadata are newly authored from RFC 8489/8445/8838;
no RFC code component, fixture or upstream implementation is imported. An authored
black-box regression reproduced destination-policy bypass through peer-reflexive
learning before the admission policy was extended to that path. The existing relay
guard could also be bypassed through learned addresses; the new independent gate
requires a selected typed relay, including explicit trickle, under relay-only policy.

The local TURN service (`relay.go`) calls public APIs from Pion TURN v5.1.2
(MIT, pinned commit `d7e65399091d5833d5d18bf265e4b0c42fd49de0`). Its original root
license was checked and already exists in linked-module notices. The module moves
from indirect to direct test use without changing its version or any transitive
version; regenerated notices and go.sum are unchanged. Listener/relay addresses and
permissions are loopback-only, credentials are randomly generated test values and
session cleanup closes its allocation owner. No server/client algorithm or example
was inspected, translated or copied. This is remote relay and independent Binding
evidence, not an implementation of our own TURN client or proof of real NAT traversal.

The isolated test peer is newly authored Go code calling the public Pion ICE/DTLS/SRTP/SCTP/DCEP APIs;
it is not a port or copy of Pion example code. `github.com/pion/ice/v4 v4.4.5` is
pinned to upstream commit `54a22240c3afddd0b32f5420a62f253f000c225c`; its original
root LICENSE is MIT. The complete Go module graph and hashes are in the peer's
`go.mod`/`go.sum`. Original linked-module notices, including MIT/BSD dependency
terms, are retained in `tests/interop/Pion/THIRD_PARTY_NOTICES.txt`.
Only trailing whitespace is normalized when collecting those notices.
The newly authored SRTP test harness also calls `github.com/pion/srtp/v3 v3.1.3`
(MIT, immutable commit `508d3e9955121cfe30d7e15791d5810d1290c438`). Its graph adds
Pion RTP v1.10.5 and RTCP v1.2.19, and selects transport/v5 v5.1.1; all three retain
their MIT notices. This graph is entirely test-only. Runtime SRTP/SRTCP code is
newly authored against RFC 3711/7714/6188 using platform cryptographic primitives.

The newly authored DTLS test harness calls `github.com/pion/dtls/v3 v3.1.9`
(MIT, immutable commit `678003b36515a142197dcfac5cc7f845ccfb1f7c`). This version
was already present in the linked test graph; it is now a direct test dependency.
Regenerating linked-module notices produced no change. No Pion DTLS implementation
or example is copied or translated into the runtime. DTLS uses .NET AES-GCM,
ECDH/ECDSA/RSA, certificate and HMAC primitives against the cited wire standards.

The newly authored channel harness uses Pion SCTP v1.12.0 (MIT, commit
`e1fbf8bf616e1c4916bc9c5c6009640856863a1d`) and datachannel v1.6.3 (MIT, commit
`2e39cf66d325476785895649d98deefb4bed04f8`). Their original root licenses and
linked graph are retained. Other linked module versions remain unchanged.
SCTP framing, association and DCEP runtime code are newly authored against the
standards. Synthetic CRC fixtures are generated by our temporary Go harness using
standard `hash/crc32`, not copied from upstream tests. The bounded four-byte ACK
exception is based on pinned Pion wire behavior; `message_channel_ack.go` at that
commit was inspected with its MIT file header to diagnose it, not imported or ported.

PR-SCTP is newly authored against RFC 3758 and extension negotiation against RFC 5061.
Our `sctp-loss.go` fault injector uses Go's standard CRC32C implementation to remove
synthetic all-0xaa DATA and corrupt selected FORWARD-TSN controls without altering
unrelated chunks. It imports no upstream test fixture or implementation. Capability
negotiation was diagnosed from authenticated test wire parameters. Pion's MIT
`datachannel.go` at the pinned commit was inspected to diagnose its ACK/public-API
lifecycle; no source was copied or translated. The test sender waits for DCEP ACK
before fault-injected partial sends and keeps that synthetic message within its
initial congestion flight; our own sender also tests abandonment of larger,
not-yet-transmitted fragmented messages.

RFC 6525 stream reset and RFC 8831 channel closure are newly authored from their
wire fields and procedures. Closure interoperability uses only the existing pinned
Pion public Close/Read/State/OPEN APIs; no reset implementation source was inspected
or imported. Our authored fault injector corrupts duplicate/out-of-range reset IDs
and recalculates CRC32C with the Go standard library. No dependency graph or notice
changes are needed for this extension.

These packages run only in an isolated local test peer. None is a dependency of the
.NET runtime library or its NuGet package. Our MIT license does not replace their
notices. Regenerate the notice file with `write-notices.sh` in the pinned Go build
stage after any test-peer dependency change and inspect the full changed graph.

The full SDP/Opus/data-channel harness (`peer.go`) is newly authored using public
Pion WebRTC v4.2.22 APIs (MIT, commit `ef0e4301807de30cf4dbf2c11a5ae15829c4ed89`).
Its graph adds interceptor v0.1.49 (MIT, commit
`a921ef919ccc48c693f30f7847325b2cb4c7212d`) and sdp/v3 v3.0.20 (MIT, commit
`8921edc83d51945017b2e1a987733a23bb9a9004`). Their pinned root licenses were
inspected and original notices retained. No implementation, examples or fixtures
were ported. RTP v1.10.5 is now directly imported by our harness; its version is unchanged.
The Go MVS graph selects x/net v0.57.0, x/crypto v0.56.0 and x/sys v0.47.0,
retaining BSD notices. Updated x/net fixes reachable DNS-parser GO-2026-5942;
x/crypto also includes the SSH fixes identified by the module scan. Govulncheck
v1.8.0 against Go 1.26.8 reports zero affected symbols and zero imported-package
vulnerabilities. The module-level GO-2026-5932 OpenPGP advisory remains without an
upstream fix; `go list -deps` verifies no OpenPGP packages are linked by this peer.
This residual is not a zero-vulnerability claim for every package in those modules.

The 60-byte Opus packet in the authored integration/AOT tests is encoded from our
synthetic 440 Hz sine with local FFmpeg 8.0/libopus, not taken from an upstream fixture.
Recipe: `ffmpeg -f lavfi -i sine=frequency=440:sample_rate=48000:duration=0.08
-ac 1 -c:a libopus -b:a 24000 -vbr off -frame_duration 20 -f ogg synthetic.ogg`.
An authored scratch reader extracts the first audio packet after the Ogg headers.
Only our synthetic encoded output is committed; no FFmpeg/libopus source or binary
is imported, linked or distributed by this library. The .NET runtime provides
encoded-media transport, not an Opus codec.

## Future permitted imports

For each imported file record the upstream URL, immutable commit, original path,
license and copyright notices, modifications and the local destination. Preserve
original notices in the source/package as required. An MIT project license does not
authorize deleting other authors' license conditions.

Pion WebRTC, DTLS, SCTP and SRTP publish MIT license files and are potential sources
for a separately reviewed port. No permission is inferred for every vendored file:
verify each selected file and its own origin before importing it.

- https://github.com/pion/webrtc/blob/main/LICENSE
- https://github.com/pion/dtls/blob/main/LICENSE
- https://github.com/pion/sctp/blob/main/LICENSE
- https://github.com/pion/srtp/blob/main/LICENSE

Current SIPSorcery package source is excluded from copying or translation due to its
additional use restriction. It may be an isolated interoperability peer; reference
implementation behavior is evidence, not permission to copy implementation code.

## Authored UDP TURN owner

TURN allocation, long-term authentication, permission/channel lifetimes, relay framing,
maintenance and disposal are newly authored from RFC8656/8489 prose/wire contracts.
No source, algorithm example, fixture or RFC code component is imported. The new
SHA256 STUN vector is independently constructed from synthetic strings and fields
using Python struct/hashlib/hmac/binascii; its header length, HMAC and CRC are authored
independently of the runtime writer. Shared malformed/lifecycle/native test fixtures
are authored protocol responders; the positive interop gate uses the existing pinned
MIT Pion TURN public APIs only. The new local HTTP service exposes random isolated
credentials only to the local test caller, reports allocation count and closes its
server/socket at session end. Go module versions/hashes/notices remain unchanged.
Legacy MD5-derived keys and SHA1 integrity serve explicitly controllable TURN
interoperability; modern SHA256 key/integrity, nonce features and USERHASH are implemented
without importing crypto code. Printable ASCII input is explicit until Unicode PRECIS
preparation is implemented. This is not a security audit or real NAT/consumer proof.

An authored stalled-control regression exposed a deletion TimeoutException escaping
DisposeAsync before key clearing/join completion. The owner now treats bounded
deletion timeout as an unacknowledged best-effort release and still completes local
cleanup; forced-close and unanswered-deletion regressions require honest diagnostics.

## Authored TURN stream boundary

TCP/TLS stream framing, serialized writes and lifetime ownership are newly authored
from RFC8656 sections 3.1/12.5/12.6 and RFC8489 wire contracts. No implementation,
upstream example or RFC code component was copied or translated. TLS delegates
cryptography and certificate validation to .NET10 SslStream/X509ChainPolicy public
APIs documented by Microsoft. Synthetic certificates are generated in memory by
our authored tests using .NET/Go certificate APIs. The synthetic stream bridge
adds fragmentation/coalescing/truncation and blocked-write faults to our existing
authored UDP responder; independent positives use the unchanged pinned MIT Pion
TURN ListenerConfig/NewServer public APIs. Go graph hashes and notices are unchanged.

Primary references: [RFC8656](https://www.rfc-editor.org/rfc/rfc8656.html),
[CertificateChainPolicy](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions.certificatechainpolicy?view=net-10.0),
[revocation mode](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions.certificaterevocationcheckmode?view=net-10.0),
[AIA policy](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.disablecertificatedownloads?view=net-10.0)
and [pinned Pion TURN public API](https://pkg.go.dev/github.com/pion/turn/v5@v5.1.2#ListenerConfig).

Windows CI of the stream milestone exposed Schannel's refusal of the synthetic
server's ephemeral ECDSA key handle. Only the authored fixture imports its generated
PFX from memory using X509CertificateLoader/DefaultKeySet on Windows, without
PersistKeySet. The temporary Windows key container is owned by certificate disposal;
PFX bytes are cleared and no private-key file is written or committed by the tests.
The fixture closes its client even when server-side authentication fails. Runtime
TLS identity, chain, expiry and purpose validation is unchanged. This uses Microsoft
public APIs ([LoadPkcs12](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificateloader.loadpkcs12?view=net-10.0),
[key storage flags](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509keystorageflags?view=net-10.0));
no upstream implementation/example was copied.

After the Windows key fix, all positive stream cases passed; three negative TLS
cases correctly rejected chain/expiry/EKU in the client but fixture cleanup surfaced
the corresponding Schannel TLS alert on its first read after server authentication.
Only explicit negative TLS fixtures consume that expected read IOException; positive
fixtures still surface it. Client AuthenticationException and zero TURN requests
remain required by unchanged negative assertions. No runtime check was weakened.

## Encoded video reassembly

The video runtime, synthetic payloads, resource/loss cases and encrypted datagram
fault proxy are newly authored from RFC 6184 sections 5–7 and RFC 7741 wire fields
in sections 4.1–4.4. No RFC pseudocode/code component, source, movie, upstream
implementation or test sample is copied. The existing pinned Pion `rtp/codecs`
public H264/VP8 payloader APIs generate independent RTP payloads from our own
synthetic bytes. The existing Go module versions, hashes and third-party notices
are unchanged; build regenerates and compares those notices. Pion is test-only.
No native/codec/NuGet runtime dependency is added. These tests establish encoded
transport reconstruction, not rendering/decoder, real provider, formal clean-room
or security-audit acceptance.

## Initial video SDP and peer routing

New video capability/format negotiation and peer routing are authored from RFC6184
section8 and RFC7741 section6 prose and field requirements. Sub-profile equivalence
and Level1b are protocol data; no RFC code component or implementation is copied.
The initial full video peer harness adds local H264/VP8 tracks using existing pinned
Pion public MediaEngine/TrackLocalStaticRTP/ReadRTP/WriteRTP APIs, not upstream
examples. Synthetic payloads only establish transport reconstruction, not valid
movie decoding. No new Go module, runtime codec, native package or notice change.
