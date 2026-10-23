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
