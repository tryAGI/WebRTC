namespace tryAGI.WebRTC;

/// <summary>Version 1, fixed-size handshake-only observations. No payload or peer identity.
/// Receive counts do not prove authentication; completed socket sends do not prove remote receipt.
/// Live fields can advance independently. Frozen snapshots survive success, failure and disposal.</summary>
public sealed record DtlsHandshakeCounters
{
    public int Version { get; init; } = 1;
    public bool Frozen { get; init; }
    public long ReceivedDatagrams { get; init; }
    public long ReceivedRecords { get; init; }
    public long RecordSendAttempts { get; init; }
    public long CompletedRecordSends { get; init; }
    public long CompletedFlights { get; init; }
    public long ValidatedFragments { get; init; }
    public long ReassembledMessages { get; init; }
    public long AcceptedMessages { get; init; }
    public long DuplicateFragments { get; init; }
    public long BufferedEncryptedRecords { get; init; }
    public long ReceivedChangeCipherSpecs { get; init; }
    public long VerifiedFinishedMessages { get; init; }
    public int PendingHandshakeMessages { get; init; }
    public int PendingHandshakeBytes { get; init; }
    public int PendingEncryptedRecords { get; init; }
}

public sealed partial class DtlsSrtpTransport
{
    private long _handshakeDatagrams, _handshakeRecords, _handshakeSendAttempts, _handshakeSent,
        _handshakeFlights, _handshakeFragments, _handshakeReassembled, _handshakeAccepted,
        _handshakeDuplicates, _handshakeBuffered, _handshakeCcs, _handshakeFinished;
    private int _pendingHandshakeMessages, _pendingHandshakeBytes, _pendingHandshakeEncrypted;
    private DtlsHandshakeCounters? _retainedHandshake;

    public DtlsHandshakeCounters GetHandshakeCounters() => Volatile.Read(ref _retainedHandshake) ?? ReadHandshakeCounters(false);

    private DtlsHandshakeCounters ReadHandshakeCounters(bool frozen) => new()
    {
        Frozen = frozen,
        ReceivedDatagrams = Interlocked.Read(ref _handshakeDatagrams),
        ReceivedRecords = Interlocked.Read(ref _handshakeRecords),
        RecordSendAttempts = Interlocked.Read(ref _handshakeSendAttempts),
        CompletedRecordSends = Interlocked.Read(ref _handshakeSent),
        CompletedFlights = Interlocked.Read(ref _handshakeFlights),
        ValidatedFragments = Interlocked.Read(ref _handshakeFragments),
        ReassembledMessages = Interlocked.Read(ref _handshakeReassembled),
        AcceptedMessages = Interlocked.Read(ref _handshakeAccepted),
        DuplicateFragments = Interlocked.Read(ref _handshakeDuplicates),
        BufferedEncryptedRecords = Interlocked.Read(ref _handshakeBuffered),
        ReceivedChangeCipherSpecs = Interlocked.Read(ref _handshakeCcs),
        VerifiedFinishedMessages = Interlocked.Read(ref _handshakeFinished),
        PendingHandshakeMessages = Volatile.Read(ref _pendingHandshakeMessages),
        PendingHandshakeBytes = Volatile.Read(ref _pendingHandshakeBytes),
        PendingEncryptedRecords = Volatile.Read(ref _pendingHandshakeEncrypted),
    };
    private void FreezeHandshakeCounters()
    {
        if (Volatile.Read(ref _retainedHandshake) is null)
            Interlocked.CompareExchange(ref _retainedHandshake, ReadHandshakeCounters(true), null);
    }
    // Written only by the handshake pump, before its terminal snapshot and cleanup.
    private void ObservePendingHandshakeState()
    {
        Volatile.Write(ref _pendingHandshakeMessages, _assemblies.Count);
        Volatile.Write(ref _pendingHandshakeBytes, _assemblyBytes);
        Volatile.Write(ref _pendingHandshakeEncrypted, _pendingEncrypted.Count);
    }
}
