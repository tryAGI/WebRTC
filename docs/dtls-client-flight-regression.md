# Client-flight DTLS loss and reorder regression

The integration suite adds six authored local UDP cases: every SRTP profile,
with and without cookie exchange, at a 256-byte transmit budget. Each case
loses one client Certificate fragment, delivers CertificateVerify before
ClientKeyExchange, delivers encrypted client Finished before ChangeCipherSpec,
and loses the first server Finished. Independent proxy counters require every
planned fault to occur. The established peers then exchange authenticated
application data and protected RTP/RTCP in both directions.

A separate negative control drops every client CertificateVerify, including
retransmissions. Both endpoints must time out, stay disconnected, and retain
zero rejected-record counts. This distinguishes missing authenticated handshake
material from a record rejected by the receiver. It does not establish the cause
of any external-provider incident.

These tests extend the existing server-flight fault coverage. They do not
relax fingerprint, signature, transcript, replay, consent or receive-size checks,
and use no external provider, microphone, playback, private signaling or media.
The wire fixtures and fault logic are newly authored; no implementation or
fixture was imported.

[RFC 6347 sections 4.2.3–4.2.5](https://www.rfc-editor.org/rfc/rfc6347.html#section-4.2.4)
describe fragment reassembly, whole-flight retransmission and CCS membership
in its associated Finished flight.

Focused execution after an admitted build:

```sh
dotnet artifacts/bin/WebRTC.IntegrationTests/release/WebRTC.IntegrationTests.dll --case-filter "DTLS client-flight"
dotnet artifacts/bin/WebRTC.IntegrationTests/release/WebRTC.IntegrationTests.dll --case-filter "DTLS missing client"
```

At the initial commit, local compilation is pending in the shared M4 compiler
queue; static `git diff --check` passes. Normal native CI must validate this
batch. Tests passing under injected loss do not prove external-provider
reliability or an end-user repair.
