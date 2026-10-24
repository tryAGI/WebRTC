# Authentication failures alongside accepted audio

`auth-load` uses real loopback ICE, DTLS and SRTP, with a single owned UDP
proxy between the nominated endpoints. After16 valid warm-up packets, the
sender offers100 audio-sized RTP packets per sample at nominal50Hz. For each
valid encrypted datagram the proxy sends0,16,64 or256 corrupted copies before
the original. Three samples cover each of the three SRTP profiles. Sender
sequence numbers and nonces remain unique; injection happens after encryption.

The corruption changes both an authenticated timestamp bit and the final tag
byte. The reader validates every admitted packet's sequence, timestamp, SSRC
and full payload. An admitted corrupted packet, duplicate or unsolicited packet
fails the process. Mutating the timestamp prevents a bad packet from silently
replacing its valid counterpart. Each scenario has a30second deadline and
owned cancellation/disposal. There are no provider calls or device sessions.

Metrics distinguish offered/received/missing valid packets, completed injected
sends, observed authentication rejections, ICE and media queue drops, process
managed allocations, CPU, GC counts and received-only latency percentiles.
Dropped UDP packets can make rejections lower than injection count. Loss censors
latency, so a small p99 does not establish success when valid traffic is lost.
A250ms settling window follows the proxy's last completed valid send; unusually
late packets can cross sample counter windows. Whole-process allocations include
the proxy and measurement harness and are not a pure crypto allocation budget.

The checker requires complete coverage and zero baseline delivery loss. Treatment
losses remain visible and do not receive an invented acceptance budget. The
`auth-load-control` suite deliberately discards the first measured valid packet
and must isolate the baseline-delivery gate with exactly one observed loss.
CI runs three fresh processes on Linuxx64/arm64, macOS and Windows, plus the
real packet-loss control. AOT is not claimed by this managed-only lane.

CSV and numeric proof JSON are job-owned artifacts retained30days for issue19
analysis. There is no persistent cache and no raw packet/payload capture. Timing
results are observations of the recorded source/runtime/hardware cohort, not a
security audit, Internet DoS resistance claim or physical audible acceptance.
