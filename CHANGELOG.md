# Changelog

Version `0.x` is experimental. Release notes are also available on
[GitHub Releases](https://github.com/tryAGI/WebRTC/releases).

## Unreleased

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
