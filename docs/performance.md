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
