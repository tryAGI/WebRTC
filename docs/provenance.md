# Source provenance

## Current authored code

All runtime code and committed tests in this initial repository are newly authored.
No source, pseudocode, test implementation or fixture has been copied or translated
from SIPSorcery, Pion or RFC appendices. Protocol constants and field layouts are
implemented from the cited standards. Authentication fixtures are independently
generated from synthetic inputs using Python's standard hmac/hashlib/binascii tools.
`src/public.snk` is a newly generated RSA public strong-name key; its private key
was discarded. It is used only for assembly identity, not publisher authentication.

This describes code provenance, not a formal clean-room or security-audit claim.

## Independent test peer

The isolated test peer is newly authored Go code calling the public Pion ICE API;
it is not a port or copy of Pion example code. `github.com/pion/ice/v4 v4.4.5` is
pinned to upstream commit `54a22240c3afddd0b32f5420a62f253f000c225c`; its original
root LICENSE is MIT. The complete Go module graph and hashes are in the peer's
`go.mod`/`go.sum`. Original linked-module notices, including MIT/BSD dependency
terms, are retained in `tests/interop/Pion/THIRD_PARTY_NOTICES.txt`.
Only trailing whitespace is normalized when collecting those notices.

These packages run only in an isolated local test peer. None is a dependency of the
.NET runtime library or its NuGet package. Our MIT license does not replace their
notices. Regenerate the notice file with `write-notices.sh` in the pinned Go build
stage after any test-peer dependency change and inspect the full changed graph.

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
