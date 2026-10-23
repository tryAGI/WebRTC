# Performance measurements

The opt-in **Performance baseline** workflow measures authenticated SRTP RTP
protect/unprotect roundtrips on native Linux x64/arm64, macOS arm64 and Windows.
It has no provider traffic or new dependencies. Local equivalent:

```
dotnet run --project src/tests/WebRTC.Performance -c Release
```

The harness warms 1,000 packets and measures seven samples of 5,000 packets for
60-byte audio-like and 1,200-byte video-like payloads across all three profiles.
Each operation checks authenticated plaintext equality and uses increasing sequence
numbers. Preallocated caller buffers exclude packet-buffer construction. Managed
bytes come from the synchronous measuring thread; timing includes validation and
comparison. Key/context creation and initial source-dictionary growth are excluded.

Results are microbenchmarks, not full transport throughput, latency percentiles,
retained/native memory, codec cost, energy, production performance or leak evidence.
Do not infer a hard timing regression from a single shared CI runner. Compare
repeated samples with matching architecture/runtime, and preserve raw CSV artifacts.
No noisy timing threshold blocks normal CI. The project is compiled by normal CI;
measurement runs only on explicit dispatch.

## AES-CM optimization under validation

Issue #12 identified 512–704 managed bytes per 60-byte protect/unprotect pair and
9.6–13.2 KiB per 1,200-byte pair across the four .NET 10.0.13 baseline platforms.
AES-GCM measured zero for these preallocated-buffer crypto-only operations.

AES-CM now owns one reusable framework ECB block transform per directional RTP/RTCP
cipher. This is the block primitive for the existing CTR construction, not ECB
packet encryption: salt, SSRC, packet index, block counter and HMAC semantics are
unchanged. The enclosing SrtpContext gate serializes all operations and disposal.
Two fixed 16-byte scratch buffers never escape and are cleared after every packet
operation and on disposal. Constructor failures also dispose the transform.
A warmed allocation regression checks authenticated RTP/SRTCP roundtrips across all
profiles; existing independent vectors, rollover, hostile inputs and lifetime tests
remain required. Independent Pion and full platform validation are required before
calling this an accepted optimization. No profile preference is changed.

## Expanded suites

Use `-- --suite packets`, `media`, `video`, or `channels` with the same project.
The default remains `crypto`. The opt-in workflow retains separate CSVs and CPU
hardware metadata on all four native platforms.

- `packets`: warmed RTP+MID parsing, truncated RTP rejection, RTCP framing and
  STUN fingerprint/attribute validation. Normal protocol CI enforces zero managed
  allocation and includes an escaping-allocation positive measuring control.
- `media`: authenticated DTLS/SRTP+ICE loopback delivery across all profiles,
  60/1,000-byte payloads, tracing disabled/enabled and default/cancellable caller
  tokens. One packet is outstanding; received bytes and zero queue drops/rejections
  are checked. Five samples of 512 packets follow 128 warmups. Process-wide managed
  allocation includes socket/queue/harness/maintenance work; p50/p95/p99 cover this
  controlled single-hop workload, not production network latency or congestion.
- `video`: bounded two-fragment H264/VP8 reassembly with preallocated input and
  checked reconstructed bytes. Input is already trusted; this excludes SRTP and
  decoding. Owned output allocations are expected and measured, not labeled zero.
- `channels`: bounded reliable SCTP data-channel delivery, including fragmentation
  for 4,096-byte messages. Process-wide allocation includes the owned peers and
  harness after connection/channel setup and warmup.

Media completion reports the managed heap after a forced collection and process
working set/private bytes where available. These whole-process observations include
runtime/native caches and do not isolate native allocations or prove leak freedom.
Unavailable process-private memory is labeled unavailable, never zero.

Initial local .NET 10.0.9/macOS arm64 media baseline (before transport changes):
AEAD AES-256-GCM median process-wide allocation was ~1,017 B for a 60-byte payload
and ~3,849 B for 1,000 bytes, default caller token and tracing disabled. Cancellable
caller cases measured ~1,033/~3,865 B. These include harness costs; use repeated
same-runtime before/after cohorts for attribution. All 65 protocol cases, including
new parser allocation contracts, passed locally. Suite compilation: zero warnings
and errors. No claim about an allocation-free complete transport follows from this.
