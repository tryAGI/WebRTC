# Changelog

Version `0.x` is experimental. Release notes are also available on
[GitHub Releases](https://github.com/tryAGI/WebRTC/releases).

## Unreleased

## [0.2.7] - 2026-10-05

- Accept bounded multiple DTLS fingerprint attributes as permitted by RFC 8122 while
  continuing to authenticate the peer exclusively with the negotiated SHA-256
  fingerprint. This restores interoperability with Simli/aiortc answers that
  advertise SHA-256, SHA-384 and SHA-512 together without weakening fingerprint
  binding. Add positive multi-algorithm and unsupported-only regression coverage.

## [0.2.6] - 2026-10-05

- Expose the loaded library's exact package version and source commit without
  reflection or consumer-inlined constants. Preserve this build identity in
  establishment evidence after cancellation/disposal and under NativeAOT.
  Verify the actual packaged DLL in a separate process before publication,
  including wrong-version/source negative controls; retain an assembly-hash-bound
  runtime proof alongside the package manifest proof.
- Accept bounded, filtered and deduplicated trickle candidates after successful
  offer/answer but before `ConnectAsync`. Preserve cancellation, disposal,
  credential generation and destination checks. Bound retained candidates even
  when an application's filter changes; account for SDP and trickle together.
- Cover empty BUNDLE tags, higher-priority non-tag decoys, both DTLS roles and
  audio/data tag orders with 16 encrypted local UDP permutations, before and
  after connect. Run bidirectional media/data and admission regressions in the
  managed integration suite and executed NativeAOT smoke.
- Record a real deployed 0.2.5 Advantage series blocked at provider call creation
  by HTTP 429 for both transports. ICE/DTLS were not started, so this is neither
  successful provider acceptance nor evidence of a new transport regression.
  DId/Simli live-provider and physical Watch playback acceptance remain open.

## [0.2.5] - 2026-10-05

- Use the negotiated BUNDLE-tagged section for remote transport candidates,
  rather than combining unrelated media-section candidates. Add a local two-route
  regression: both routes answer ICE, but only the tagged route originates bundled
  DTLS. Preserve wrong-source rejection, bidirectional DCEP and authenticated
  media after a rejected packet. Cover tag/media ordering and a rejected suggested
  tag; execute the fixture under NativeAOT and against the pinned published baseline.

- Retain seven exact ICE data-admission rejection counters independently of packet
  capture, including no nomination, unavailable/mismatched path, wrong source,
  oversize, expired consent and shutdown. Export immutable, address-free snapshots
  through ICE and peer establishment evidence. Preserve existing admission checks
  and trace categories; add actual host/relay rejection, truncation and disposal
  regressions to managed and executed NativeAOT tests.

- Record a fresh real-provider timeout with incoming DTLS data rejected before
  parsing. The BUNDLE route regression is reproduced locally; its relationship to
  that provider's signaling remains unproven until deployed repeat acceptance.

## [0.2.4] - 2026-10-04

- Include outgoing DTLS handshake records and retries in opt-in packet socket
  boundaries; classify incoming DTLS at ICE demultiplexing without treating the
  label as authentication. Exercise silent/retrying and successful local peers,
  with no packet identifiers, under managed and executed NativeAOT tests.
- Record fresh Advantage phase evidence isolating DTLS-client ServerHello timeouts
  before SCTP. The provider root cause and repeat acceptance remain open.

- Exercise simultaneous SCTP initiators when one or both initial encrypted INIT
  records are dropped. Verify bidirectional DCEP data, preserved DTLS stream
  parity and handshake retransmission in managed and executed NativeAOT tests.

## [0.2.3] - 2026-10-04

- Initiate SCTP from both WebRTC endpoints regardless of DTLS setup, as required
  by RFC 8841 section 9.3. Previously a DTLS server waited passively, allowing
  a second passive SCTP peer to stall after successful ICE/DTLS. Add a pinned
  published-0.2.2 reproduction, positive control and fixed-source regressions
  with passive and simultaneously initiating peers in both DTLS roles, retaining
  DTLS-based DCEP stream parity. Execute fixed regressions under NativeAOT too.

- Require managed protocol/network regressions and published/executed whole-root
  NativeAOT on native Linux arm64 CI, matching the Advantage establishment canary.
- Require the pinned independent Pion interoperability suite on native Linux arm64
  in addition to x64, with host and container architecture assertions.

## [0.2.2] - 2026-10-04

- Retain bounded ICE/DTLS/SCTP/local-DCEP establishment evidence after timeout and
  disposal, including expected handshake steps, elapsed times and retries. Preserve
  negotiated SRTP profile after shutdown and classify total deadlines as timeouts.
  Add local isolated-failure, repeated-peer and history-bound regressions to executed
  NativeAOT. See [Advantage issue #3 acceptance](docs/establishment.md).

- Wait for the secure local Chromium document before starting the audio/video
  interoperability probe, instead of racing its provisional about:blank context.
- Reconcile parallel main/tag NuGet publication conflicts with bounded index retries;
  require matching version, source commit and DLL before accepting an existing package.

## [0.2.1] - 2026-10-04

- Preserve ICE URI total-deadline timeout classification when timer callbacks are
  delayed behind a per-address timeout; distinguish exhausted server attempts in tests.
- Fix RTCP PLI throttle validation to compare only within the valid observation
  window, and cover deliberately delayed observers in the normal and NativeAOT suites.
- Version 0.2.0 was not published: its release validation hit this scheduling-sensitive
  test assertion. The original tag is preserved; 0.2.1 also includes the ICE deadline fix above.

- Add opt-in, bounded versioned packet-stage diagnostics with live attachment,
  sampling, expiry, cancellation, queue/drop attribution and consumer-owned .NET
  Meter/Activity publication outside media loops.
- Trace host/TURN UDP/TCP/TLS receive boundaries, secure receive/audio queues and
  audio/video send locks, protection and local send completion; preserve explicit
  unknown kernel/TLS/socket timing and caller-owned pacing.
- Expose non-mutating per-source RFC 3550 reception snapshots, remote reception
  reports about transmitted streams, RTT age/staleness and route/consent evidence.
- Add deterministic local fault/lifecycle/collector tests, NativeAOT execution and
  measured paced-Opus allocation/CPU/delivery reports. See [diagnostics](docs/diagnostics.md)
  and [release measurements](docs/diagnostics-measurements.md).

## [0.1.1] - 2026-10-04

- Fix documentation, license and contribution links in the NuGet README.
- Reject relative README links in the package publication guard.
- Resolve changelog links against the release tag in GitHub release notes.

## [0.1.0] - 2026-10-04

- Initial framework-only .NET 10 transport: bounded STUN/ICE/TURN, authenticated
  DTLS/SRTP/SRTCP, SCTP/DCEP channels and explicit Opus/H264/VP8 negotiation.
- Independent local Pion and Chromium interoperability, decoded media/loss recovery,
  protocol/resource-bound regressions and executed NativeAOT validation.
- Bounded ICE server URI/DNS gathering with destination admission and TLS identity.
- Normal main/tag publication with source proof and a mandatory `0.x` version guard.

See [acceptance gates](docs/acceptance.md) for remaining consumer/device work.
