# Diagnostics measurements for 0.2.0

Measured on 2026-10-04 at source commit
`b4798b5a63115f6baa0f4a92c39096edf6a426ef`.
[All validation jobs passed](https://github.com/tryAGI/WebRTC/actions/runs/37212018263).
[Raw results](diagnostics-measurements.json) retain every round and its job link.

## Experiment

Each mode uses two real local peers exchanging 60-byte encoded Opus packets.
A segment warms up with 50 packets, then measures 100 packets submitted at an
intended 20 ms cadence. Three rounds rotate `off`, `aggregate` and `trace` order.
The aggregate mode publishes to no-op framework listeners; trace additionally
drains its bounded buffer using reused consumer storage. Measurement includes
both peers, protocol processing and explicit publication, excludes capture setup,
and excludes codecs, exporters, providers and physical playback.

CPU and allocations are process-wide totals, not isolated instrumentation costs.
Delivery latency is caller submission to the receiving audio iterator yielding.
It is not wire, kernel, decoder or speaker latency. No packet Activity is enabled
by default; diagnostic publication remains consumer-owned.

## Median of three rounds

The latency columns are medians of per-round percentiles, not pooled percentiles.
Bytes/packet divide total process allocation by the 100 delivered audio packets.

| Lane | Mode | Wall ms | CPU ms | Bytes/packet | Delivery p50 ms | Delivery p99 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Independent Pion ICE and SRTP peer | off | 1999.88 | 112.36 | 4540.96 | 0.035 | 0.058 |
| Independent Pion ICE and SRTP peer | aggregate | 2000.42 | 63.22 | 5736.16 | 0.036 | 0.057 |
| Independent Pion ICE and SRTP peer | trace | 1999.47 | 62.95 | 5702.72 | 0.036 | 0.054 |
| Protocols (windows-latest) | off | 2008.84 | 218.75 | 4190.88 | 0.136 | 0.256 |
| Protocols (windows-latest) | aggregate | 2006.98 | 46.88 | 5485.36 | 0.135 | 0.243 |
| Protocols (windows-latest) | trace | 2000.53 | 78.12 | 5446.64 | 0.133 | 0.221 |
| Protocols (ubuntu-latest) | off | 2000.53 | 125.14 | 4752.64 | 0.099 | 0.180 |
| Protocols (ubuntu-latest) | aggregate | 2000.80 | 126.91 | 5676.56 | 0.097 | 0.178 |
| Protocols (ubuntu-latest) | trace | 1999.54 | 131.76 | 5738.72 | 0.099 | 0.178 |
| Protocols (macos-latest) | off | 2883.62 | 160.37 | 4950.80 | 0.352 | 10.383 |
| Protocols (macos-latest) | aggregate | 2923.46 | 217.97 | 6231.92 | 0.353 | 10.611 |
| Protocols (macos-latest) | trace | 2755.78 | 147.12 | 6005.12 | 0.387 | 10.473 |

## Interpretation and limits

- Aggregate/trace modes allocate more than off in these runs: about 0.9–1.3 KiB
  additional process allocation per delivered packet when comparing mode medians.
  This includes measurement publication; it does not measure capture buffer setup.
- These short shared-runner samples do not establish a CPU improvement. First-round
  off CPU remained elevated despite warm-up, and OS scheduling, JIT and unrelated
  process work still affect the results. Rotating order reduces but cannot remove
  those confounders. Raw CPU ranges are intentionally retained.
- macOS took 2.7–3.2 seconds per 100 packets at an intended two-second cadence.
  Its trace delivery p99 ranged from 9.900 to 22.696 ms. Timers and host scheduling
  therefore matter; these measurements are not a latency SLA or zero-cost claim.
- The extra Pion-container lane runs the same local measurement within its
  independent interoperability job. It is not a measurement of Pion media latency.
- Counters remain exact for recorded stage calls while bounded trace/duration
  records may drop; snapshots expose those diagnostic drops separately from media
  loss. See [the contract and coverage limits](diagnostics.md).

## Validation scope

The source above passed 63 protocol cases and 275 local-network cases on Linux,
macOS and Windows; the Pion lane passed 404 cases. Chromium decoded media and
exercised loss recovery; whole-library-rooted Linux NativeAOT was published and
executed, including diagnostic boundary, lifecycle and fault cases.

Live attachment/replacement, cancellation/expiry, queue overflow, diagnostic ring
overflow, slow/throwing listeners, privacy/cardinality, caller pacing, RTP rollover
and source restart have local regression coverage. Fault cases separately inject
pre-managed delivery delay, a managed receive-handler stall, a secure-processing
queue wait, reorder/replay, authentication failure and loss.

Kernel receive timestamps and socket overflow counters remain unsupported and
explicitly reported as such. TURN TLS exposes managed decrypted-stream reads, not
raw encrypted socket arrival or internal TLS time. Video-frame/SCTP packet-stage
queues are outside version 1 coverage. Provider/device audio acceptance remains
separate future work; no paid endpoint or device was used for these results.
