# Independent Chromium interoperability

Run `./tests/interop/Chromium/run.sh` from the repository. Docker is required.
The script builds the owned peer, starts disposable containers on a unique **internal**
Docker network, and removes its containers/network/image even on failure. No host
ports, user profile, credentials, environment files, STUN/TURN service or provider is used.
Only the exact isolated interface and loopback destinations pass candidate admission.
Image download/build may use the internet; the actual test containers have no external route.

The authored C# peer uses the public library APIs. An authored Node script uses Node's
built-in WebSocket/HTTP and Chromium's public DevTools protocol, WebRTC and WebCodecs
APIs. No npm package is installed. Chromium 151.0.7922.34 is supplied by the pinned
Playwright v1.62.1 image (its index SHA256 is in the script); Playwright APIs are not used.
The .NET SDK image is pinned separately in the Dockerfile. The launcher checks
the pinned image's `chrome-linux` (arm64) and `chrome-linux64` (x64) locations
and fails immediately with browser stderr when startup fails.

Every scenario runs for both VP8 and H264 and must pass, without skips:

- Browser offer with owned DTLS client and server, then owned offer with browser answer.
- Fingerprint-bound ICE/DTLS/SCTP, reliable channel messages in both directions.
- Six concurrent browser-opened DCEP policy combinations, preserving ordering and
  retransmission/lifetime metadata. Echo Unicode text, empty text/binary and binary
  messages; reliable payloads of 32 KiB require actual SCTP fragmentation.
- Close/reset a browser channel and explicitly reuse its ID. Open a timed unordered
  channel from the owned peer, inspect its browser policy and exchange messages,
  then close it from the owned side. Before video, require exactly nine admitted
  generations, eight confirmed clean closures and 34 received application messages.
- Bidirectional Opus at 48 kHz using authored two-tone/silence signals. Browser-sent
  packets traverse the owned SRTP receiver and are decoded through WebCodecs;
  owned-sent packets traverse the browser's actual WebRTC receiver and are measured
  as PCM by an AudioWorklet. Require the distinct tones in order, final silence,
  bounded packet/clock evidence and browser decoded-sample statistics. Silent and
  wrong-frequency negative controls must fail. The container uses a zero-volume
  audio element to start browser playout and a silent Web Audio sink for observation;
  this is decoder evidence, not audible speaker or physical-device acceptance.
- An independently authored 320×240 gray scene with a moving black marker, encoded
  as VP8 or H264 constrained baseline by the test browser and sent through the owned RTP/SRTP peer. Small fragments
  deliberately require browser RTP reassembly. The video element must render the first
  key frame with expected dimensions and pixels.
- Drop one actual protected video RTP packet in the local UDP proxy. Browser PLI must
  reach the owned source and application event, after loss is armed. The application
  then asks the still-live encoder for a **fresh** key frame. Browser pixels and stats
  must confirm recovery, followed by ordinary delta frames reaching the final marker.

The clip is generated per run; no upstream codec fixture, source or algorithm is copied.
The distributed tool images retain their upstream notices and licenses. They are test
infrastructure, not dependencies or assets included in `tryAGI.WebRTC`.
`--no-sandbox` applies only to the disposable isolated container browser.

This covers VP8 and constrained-baseline H264 mode 1 initial negotiation, not general renegotiation/restart,
real NAT traversal, audible device playback, provider sessions or physical Watch delivery.
The pinned x64 browser supplies both codec encoders and is the default on all
hosts, using Docker emulation when required. The owned .NET peer uses the host
architecture. An explicit `TRYAGI_CHROMIUM_PLATFORM=linux/arm64` override is available
for diagnosis; that distribution lacks H264, so the H264 case must fail, not skip.
Only linux/amd64 and linux/arm64 overrides are accepted.

H264 uses independently authored AnnexB splitting and RFC 6184 single-NAL/FU-A
framing in the test harness. Mode 1 and profile `42e01f` are advertised with explicit
level asymmetry support. Browser answers requesting that capability must still pass
strict negotiation; no answer check is suppressed. The encoder may emit a compatible
lower level. Parameter sets and IDR are carried over real protected RTP, not inserted
by the receiver. Mode 0, other profiles/levels and general H264 codec coverage remain.

Large browser SCTP packets exposed 1225-byte protected datagrams. ICE and DTLS now
have separate bounded receive budgets (default 2048) while local sends remain 1200.
Chromium's unordered one-byte DCEP ACK is accepted only for a local unordered OPEN;
strict raw-peer negatives cover ordered channels, padding, unknown/incoming streams
and unordered OPEN. This is a documented wire exception, not full RFC compliance
by the browser. Browser PR loss/abandonment remains covered only by the separate
Pion fault lane at this milestone; the browser's PR exchanges here have no forced loss.
