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
