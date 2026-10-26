# Caller-owned receive buffers: proposed API design

Status: proposed, no runtime API or existing owned-array contract changed.
Implementation tracker: [WebRTC#26](https://github.com/tryAGI/WebRTC/issues/26).
The objective is lower warmed receive allocation without making packet ownership
implicit. The existing SecureMediaDatagram.Data array can outlive subsequent
receives. Reusing it would silently corrupt callers; it must retain that contract.
Payload-bearing framework GCM rejection allocations are a separate API constraint.

## Proposed opt-in contract

Capture an init-only receive-mode option at transport construction: existing
owned datagrams by default, or caller-buffer mode. Reject using both reader
APIs on one transport. Retain exactly one outstanding receive operation, matching
the existing single-reader queue. A second concurrent receive fails immediately.

Proposed signature, deliberately not shipped:

```csharp
public ValueTask<MediaReceiveResult> ReceiveMediaIntoAsync(
    Memory<byte> destination, CancellationToken cancellationToken = default);

public readonly record struct MediaReceiveResult(
    MediaReceiveStatus Status, SecureMediaKind Kind, int Length, int RequiredCapacity);

public enum MediaReceiveStatus { Received, DestinationTooSmall, Completed }
```

The caller lends exclusive access to destination until the ValueTask completes,
faults or cancels. Only after completion may the caller read, modify or reuse it.
The transport must never retain that Memory or access it after completion. Awaiting
an operation does not transfer ownership of any pooled internal buffer to callers.
No callback executes under the receive/crypto lock or on the network pump.

On Received, only destination[..Length] contains the authenticated packet. On
DestinationTooSmall, report exact RequiredCapacity, write nothing, consume and
release that one packet, and record an explicit local media drop/diagnostic reason.
A retry receives the next packet; this status is not a promise to retain the same
packet. This deliberate datagram contract avoids indefinite head reservations,
extra held buffers and head-of-line blocking from an undersized destination. The
caller should provision the configured MaximumReceiveDatagramSize as a
conservative destination bound for accepted SRTP/SRTCP plaintext. Existing
owned-array mode is unaffected. On Completed, Length and RequiredCapacity are 0. Kind is meaningful only for
Received and DestinationTooSmall. Faulted transport completion throws its original
error; caller cancellation throws OperationCanceledException.

Cancellation before dequeue/copy writes nothing and leaves queued media unchanged.
After the synchronous copy starts, complete Received atomically rather than report
cancellation after altering the caller's memory. Shutdown races follow the same
rule; each internal buffer has one owner and exactly one release. Document that
uncooperative caller mutation while a receive is pending violates the contract.
If a custom/disposed MemoryManager throws while obtaining or writing its Span,
fault the receive and still clear/return the internal rental exactly once; never
retain the caller memory for a later retry.

## Internal ownership and security

Use a bounded queue of value-type entries holding rented byte arrays, valid length,
kind and diagnostic trace. Existing stack scratch remains the unauthenticated
boundary: verify first, rent/copy/enqueue only after authentication succeeds. Do not
commit replay/source changes before existing verification rules permit them.
Avoid a per-packet heap record or IMemoryOwner wrapper. Preserve the old owned mode
separately; do not pool arrays that have escaped through its public API.

Every dequeue/copy, DropOldest eviction, failed enqueue, cancellation after
ownership transfer, shutdown and fault path must clear the full rented array with
CryptographicOperations.ZeroMemory and return it exactly once. Count buffers held
by the pump, queue and pending receive in a fixed byte/entry budget. Charge actual
rental length, not requested payload length. Do not depend on
ArrayPool's global retention policy as that transport budget. Use a private framework ArrayPool.Create with finite bucket retention, separately
accounting for retained pool capacity and active queue rentals. Do not expose pool
leases publicly. A stronger custom allocator needs a separate reviewed design.

This still copies authenticated scratch into internal storage and then into the
caller destination. It is an allocation reduction proposal, not zero-copy. Cold
pool misses, linked cancellation registrations, asynchronous completion machinery
and native socket/crypto costs remain separately measured. A warmed exact-zero
claim must be established per completed receive configuration, never inferred from
using ArrayPool or ValueTask.

## Required implementation evidence

- Independent peers deliver identical RTP/RTCP bytes and diagnostics in both modes;
  retained owned arrays stay unchanged after later receives.
- A tracking pool proves one clear/return for every success, overflow, failed write,
  undersized destination, cancellation, shutdown and fault race. Poisoned reused buffers
  and unrelated subsequent packets must not escape as authenticated data.
- Invalid tags, wrong keys, replay and truncation preserve rejection-state and
  destination guarantees; original valid packets still succeed. An undersized
  receive consumes an already-authenticated packet and does not roll back its
  replay admission; it never changes crypto state independently. Preserve the
  current framework-only crypto policy.
- Test pending cancellation, queued cancellation and copy/close races, concurrent
  readers, forbidden mixed modes and exact memory caps.
- Compare same-runtime/source-controlled before/after allocation and CPU cohorts,
  including independent fresh-process holdouts and cold/pool-pressure controls.
  Cover JIT/NativeAOT on all four platforms; do not raise existing ceilings.

A synchronous borrowed-span callback was considered and rejected as the default:
callers could block the network pump and interfere with valid audio. An exported
pooled lease makes disposal mandatory and exposes use-after-return risks through
retained Memory. The caller-buffer proposal narrows ownership while keeping user
code outside the pump. Review this contract, including the explicit short-buffer consume/drop policy,
and establish failing lifetime tests before changing runtime APIs.
