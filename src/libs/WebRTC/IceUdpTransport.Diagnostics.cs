namespace tryAGI.WebRTC;

public sealed partial class IceUdpTransport
{
    private bool _ownsDiagnostics;
    private readonly Guid _diagnosticEpoch = Guid.NewGuid();
    /// <summary>Lower-level ICE capture for applications owning their DTLS/SRTP layer. Does not authenticate media.</summary>
    public PeerDiagnosticSession AttachDiagnostics(PeerDiagnosticsOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        lock (_gate)
        {
            ThrowIfStopped();
            var capture = new PeerDiagnosticSession(_diagnosticEpoch, options, cancellationToken) { ConnectionStartedAt = _startedAt };
            _ownsDiagnostics = true; Interlocked.Exchange(ref Diagnostics, capture)?.Dispose(); return capture;
        }
    }
    internal IReadOnlyList<QueueEvidence> QueueEvidence()
    {
        var capture = Volatile.Read(ref Diagnostics);
        var at = _datagrams.Reader.TryPeek(out var packet) ? packet.Trace.LastStageTicks : 0;
        var result = new List<QueueEvidence> { new(PacketStage.IceEnqueued, null, _datagrams.Reader.Count,
            capture?.QueueHighWater(PacketStage.IceEnqueued), at == 0 ? null : System.Diagnostics.Stopwatch.GetElapsedTime(at)) };
        lock (_gate) foreach (var path in _paths) if (path.Relay != null) result.Add(path.Relay.QueueEvidence(capture));
        return result.AsReadOnly();
    }
    public void DetachDiagnostics() { _ownsDiagnostics = false; Interlocked.Exchange(ref Diagnostics, null)?.Dispose(); }
}
