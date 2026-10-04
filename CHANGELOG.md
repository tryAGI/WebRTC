# Changelog

Version `0.x` is experimental. Release notes are also available on
[GitHub Releases](https://github.com/tryAGI/WebRTC/releases).

## Unreleased

No changes recorded yet.

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
