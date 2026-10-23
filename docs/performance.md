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
