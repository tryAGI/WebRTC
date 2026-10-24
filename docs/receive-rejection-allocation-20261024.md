# Bounded secure receive ownership and ready-input processing

Issue22's loopback stress benchmark exposed valid-packet loss at the bounded
ICE queue under macOS bad-tag bursts. The first candidate change keeps the
queue policy, bounds and authentication/replay implementation unchanged.

DTLS receive now skips allocating/awaiting a WhenAny promise when input or its
maintenance tick is already complete. It still examines maintenance after each
datagram and explicitly checks lifetime cancellation on every iteration. This
preserves timer fairness and bounds disposal under continuously ready input.

For RTP/SRTCP, a bounded stack span receives authenticated output. SRTP's existing
4096byte maximum is checked before reserving stack storage, independently of
the larger configurable DTLS receive limit. Scratch is scrubbed in finally.
An owned plaintext array is created only after successful authentication and
never aliases scratch or another queued payload. The complete transport still
allocates for UDP ownership, async processing and nonempty GCM framework failures;
this is not a whole-pipeline zero-allocation claim.

New managed and rooted AOT tests retain a valid packet across32 subsequent
receives, reject over-SRTP-limit input, then verify valid state and bounded
disposal during concurrent invalid traffic for all three profiles. The invalid
producer is explicitly cooperative; a prototype synchronous producer accidentally
ran until the test deadline, so the corrected test also checks the deadline
before/after disposal to prevent that false pass.

Local strict compilation completed with zero warnings. Focused execution and
matched before/after stress, ordinary managed/independent/AOT gates and allocation
budgets must complete before any performance or deployment acceptance claim.

## Validation and measured limits

Source9c28adfbac589603e5ec972213bf5aed3442cd50 passed all nine functional
lanes in [run40064322954](https://github.com/tryAGI/WebRTC/actions/runs/40064322954):
managed protocol/network, independent Pion on native Linuxx64/arm64, Chromium,
and rooted NativeAOT execution. The new ownership/bounds/disposal cases passed
all three profiles. The macOS NativeAOT lane still emitted two missing SDK-module
warnings, so this is functional evidence, **not** zero-warning quality acceptance.
The focused local execution did not start before its admission deadline; local
strict compilation did finish with zero warnings/errors.

The matched workload at958db3e342820da5a6e7ea740fdb645dd90490b4 in
[run40063880939](https://github.com/tryAGI/WebRTC/actions/runs/40063880939)
reduced median whole-process allocations at burst256 by16.0–33.0% across the
recorded platform/profile cells. This includes the harness/proxy and differing
processed counts under loss; it is not a crypto-only allocation or throughput
claim. Linux/Windows lost no valid packets, but macOS lost1662/10800 versus
1601/10800 previously. Four-platform active allocation contracts passed separately
in run40063882777. Production adoption remains deferred: the allocation change
has not solved macOS hostile-load audio continuity or the compiler quality gate.
