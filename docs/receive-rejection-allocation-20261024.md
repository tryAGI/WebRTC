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
