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
sends, aggregate secure-transport rejections, ICE and media queue drops, process
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

## Measured result

[Run40060836414](https://github.com/tryAGI/WebRTC/actions/runs/40060836414)
(source`b53e3860867cae99490896b456b1940c9890d347`) completed all four
managed lanes with zero compiler warnings:432 normal samples, three fresh
processes per platform,3,628,800 completed corrupted sends. Every platform's
real packet-drop control detected exactly the intended missing baseline packet.
No observed corrupted plaintext or duplicate reached the accepted collector.
Raw CSV was downloaded and all normal/control checks independently recomputed.

| Cohort | Valid packets missing /10800 offered | ICE queue drops | Observed rejections /907200 injected |
|---|---:|---:|---:|
| Linuxx64 |0|10755|896445|
| Linuxarm64 |0|1124|906076|
| Windowsx64 |0|884|906316|
| macOSarm64 |1601|333806|574995|

The macOS loss occurred only under treatment. Its baseline and burst16 had no
valid loss. Burst64 lost20/284/309 valid packets for AES-CM/AES128GCM/AES256GCM;
burst256 lost86/460/442 respectively (900 offered per cell). At256, median
received-only p99 was21.105/25.753/24.369ms. No secure-media queue drops occurred.
These are recorded hosted-runner cohorts, not a matched cross-OS speed ranking.

The workflow's coverage/integrity pass does **not** establish hostile-load
reliability on macOS. [Issue22](https://github.com/tryAGI/WebRTC/issues/22)
tracks that defect and the pre-authentication plaintext-array allocation
candidate. Process allocations were nonzero across all platforms; AES-CM's
crypto-only zero contract does not imply a zero-allocation UDP/DTLS pipeline.
The existing supported framework API still throws for nonempty AES-GCM tag
failure; no crypto, authentication or replay policy was changed in this batch.

A separate admitted local macOS27/.NET10.0.9 control also observed99/100 then
100/100 and100/100 valid packets with exactly one intentional drop. Its harness
source metadata is `working_tree_unverified`, so it is only local semantic
confirmation and is excluded from the committed-source CI cohort/proof.

All432 normal samples have zero packet-accounting residual individually:
`offered + injected - received - rejected - ice_drops - media_drops - fixture_drops`.
The macOS excess ICE drops therefore match its observed valid-packet loss in
these cohorts. This supports the queue as the observed loss location, not the
cause of receiver slowness or general absence of kernel loss. Rejection counts
are aggregate secure-transport failures (including replay/framing), not a count
of cryptographic API invocations. The checker retains nonzero residuals as
observations in future cohorts; kernel loss or crossing counter windows can
produce them without proving fixture corruption.

## Receiver allocation candidate and producer pacing controls

Run40063880939 at958db3e342820da5a6e7ea740fdb645dd90490b4 re-executed
three processes on each platform after bounded receive scratch and ready-input
wait changes. Process allocation fell across the recorded workloads, but macOS
still lost1662 valid packets (previous1601); Linux/Windows again lost none.
This is an allocation improvement, **not** acceptance of the macOS loss fix.
The four-platform active-allocation budget run40063882777 passed separately.

Source inspection also identified a fixture confounder: the original absolute
producer deadline catches up after stalls by sending valid packets back-to-back,
which also bunches each associated invalid burst. The updated fixture preserves
that explicit `deadline` control and adds an `interval` producer which schedules
each new send at least20ms after the previous send start. It does not accumulate
catch-up work. Both controls retain the same corruption, load counts, validation
and real packet-drop control. No historical treatment loss is erased or relabeled.

New observations include minimum/maximum producer send gap and maximum proxy
injection duration per sample. Those clocks describe managed send/proxy work,
not kernel packet arrival. Source/pacing labels remain explicit; results from
different producer modes must not be presented as matched runtime improvements.
Interval captures fail if their measured minimum gap falls below19.9ms; the
original deadline control retains its measured bunching without suppression.
The default manual lane interleaves three fresh processes of each producer mode
on the same runner per platform, with a finite25minute job bound.
