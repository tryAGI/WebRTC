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
The .NET SDK image is pinned separately in the Dockerfile.

Every scenario must pass, without skips:

- Browser offer with owned DTLS client and server, then owned offer with browser answer.
- Fingerprint-bound ICE/DTLS/SCTP, reliable channel messages in both directions.
- An independently authored 320×240 gray scene with a moving black marker, encoded
  as VP8 by the test browser and sent through the owned RTP/SRTP peer. Small fragments
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

This covers VP8 and initial negotiation, not H264, general renegotiation/restart,
real NAT traversal, audio playback, provider sessions or physical Watch delivery.
The pinned Chromium build does not offer H264 or support its WebCodecs encoder;
H264 decoder acceptance remains an explicit separate gate, not a skipped green case.
