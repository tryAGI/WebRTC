# Idle maintenance and native identity construction

Baseline source: `4de6c4adfe9e077817d70cc0d02dfb6fcc8dadaf`.
[Four-platform baseline](https://github.com/tryAGI/WebRTC/actions/runs/40005348121)
completed successfully. Raw artifact CSVs retain runtime, OS, architecture, source,
hardware and tracing configuration. These are synthetic local peers, not provider
or device acceptance. Different machines are not an OS speed ranking.

## Attribution before changing the runtime

Median of 24 identity samples, .NET 10.0.13:

| CI platform | Legacy certificate creation ms | Public-only certificate creation ms | Existing whole identity ms |
|---|---:|---:|---:|
| macOS arm64 | 77.572 | 0.247 | 78.818 |
| Linux arm64 | 0.484 | 0.266 | 0.639 |
| Linux x64 | 0.401 | 0.187 | 0.487 |
| Windows x64 | 1.618 | 0.227 | 2.407 |

The split certificate columns exclude key generation, request construction,
verification and disposal. The whole-identity column includes generation and
fingerprint hashing; these columns must not be presented as equivalent spans.
A separate local macOS 27.0.1/.NET 10.0.9 cohort shows hundreds of milliseconds
in the legacy certificate phase and around 0.1 ms in the public-only phase.

The existing identity already owns its fresh ECDsa key separately. The
CreateSelfSigned convenience API unnecessarily associates a private key with the
temporary certificate. Explicit CertificateRequest.Create signs the public
certificate with that same fresh key, avoiding the association. CN, P-256,
SHA256, five-minute clock allowance and 30-day validity remain. A random positive
16-byte serial is generated per certificate. No private key is exported,
persisted or shared across identities.

In fresh processes, tiered compilation disabled for attribution only, after 32
warmups and eight waves of 16 constructions/disposals with full collections,
macOS RSS rose 1,572,864 bytes from first to last legacy wave, versus 81,920 bytes
for public-only and 98,304 for empty control. The existing runtime rose 1,474,560
bytes. GC committed bytes stayed unchanged in those macOS cohorts. This isolates
a native certificate-path retention contribution; eight finite waves do not
prove a leak, eventual plateau, or explain all full-peer RSS growth. Linux
controls also grew; Windows controls decreased. Never subtract managed committed
bytes from RSS and label the remainder a precise native allocation count.

## Deadline scheduling

Baseline one-pair/no-trace median process managed bytes/s and completed
threadpool items/s across profiles/samples:

| CI platform | Bytes/s | Threadpool items/s |
|---|---:|---:|
| macOS arm64 | 83,473.94 | 127.36 |
| Linux arm64 | 106,053.41 | 187.97 |
| Linux x64 | 104,168.75 | 187.01 |
| Windows x64 | 97,440.49 | 169.45 |

These include harness work, consent and all runtime tasks. Completed threadpool
items are a scheduling proxy, not measured kernel wakeups or device energy.

The change preserves DTLS's 20 ms handshake/retransmission loop until
establishment. Thereafter clients disable ticks; servers retain a four-minute
final-flight cleanup deadline then disable ticks. Incoming duplicate flights,
media and cancellation still use the immediate receive path. A queued old tick
may be delivered once when the period changes.

ICE retains existing checklist pacing before selection. Once selected it waits
for the earliest consent-send or absolute consent-expiry deadline, respecting
minimum check pacing. Existing candidate/path signals and cancellation interrupt
the wait. Positive sub-millisecond waits round up rather than spin. Consent
intervals, random jitter, expiry and synchronous send admission are unchanged.
Existing delayed-reply, loss, revocation and independent-peer tests remain gates;
a new selected-state test checks prompt delivery/disposal while waiting.

After-change measurements and exact-source independent CI acceptance must be
recorded before a consumer package is adopted. Lower idle work is not a claim
that the complete transport allocates zero bytes.

## Matched after-change benchmark cohort

[After artifacts](https://github.com/tryAGI/WebRTC/actions/runs/40006120745),
source `5cb155d185a5de2b535769ba9f48e828112b95be`, passed on all four
platforms. Same runtime/platform/runner classes, separate hosted jobs: timing
variance and hardware noise remain. One-pair/no-trace medians:

| CI platform | After bytes/s | After threadpool items/s | After whole identity ms |
|---|---:|---:|---:|
| macOS arm64 | 43,609.89 | 25.34 | 0.669 |
| Linux arm64 | 55,027.80 | 41.99 | 0.407 |
| Linux x64 | 59,369.42 | 46.49 | 0.380 |
| Windows x64 | 50,279.52 | 30.99 | 1.032 |

Observed idle managed allocation fell 43–48%, completed threadpool work 73–82%.
This remains nonzero; no whole-transport zero-allocation or energy guarantee.
Mac audio-only/AES-256-GCM median two-peer construction fell 94.5545 -> 1.9015 ms;
complete setup fell 172.7335 -> 99.3445 ms. Different spans must not be summed.
The optimized macOS runtime identity RSS rose 114,688 bytes across the measured
wave range, with the last three waves equal. The Linux controls also plateaued
in their last three waves, while Windows RSS decreased; no leak-freedom claim.

A matched local Mac cohort showed 102,455.62 -> 46,086.89 managed bytes/s and
178.44 -> 27.98 threadpool items/s. Local solution build had zero warnings/errors;
66/66 protocol cases, three consent expiry/delayed-reply/revocation cases and the
selected idle delivery/disposal case passed. Local full-solution admission first
timed out behind another owner, then passed in 107.662 seconds. Focused test and
benchmark admission completed in 107.041 seconds. The earlier benchmark-only
build passed with zero warnings but its 235.165-second execution receipt breached
the build SLO; that scheduling result is retained separately from correctness.

The isolated local identity retention controls also reproduced the native-path
difference: legacy RSS 60,424,192 -> 61,898,752 bytes, public-only 55,443,456 ->
55,508,992, optimized runtime 55,001,088 -> 55,083,008, control 48,758,784 ->
48,840,704; managed committed bytes were constant per process. The last three
optimized runtime/public-only/control waves were flat. Longer whole-peer
retention and allocator attribution remain in issue #10.
