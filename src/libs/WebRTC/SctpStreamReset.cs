using System.Diagnostics;

namespace tryAGI.WebRTC;

/// <summary>A reset in the serialized receive stream, after earlier data in that direction.</summary>
public sealed record SctpStreamReset : SctpReceiveEvent
{
    public IReadOnlyList<ushort> StreamIds { get; }
    public bool Outgoing { get; }
    internal SctpStreamReset(ushort[] streams, bool outgoing)
    { StreamIds = Array.AsReadOnly(streams); Outgoing = outgoing; }
}

public sealed partial class SctpAssociation
{
    private const int MaximumResetEvents = 128;
    private bool _peerStreamReset;
    private uint _nextResetSequence, _nextPeerResetSequence;
    private int _resetEventReservations;
    private TaskCompletionSource _resetAdmission = NewSignal();
    private readonly HashSet<ushort> _resettingStreams = [];
    private readonly Dictionary<uint, ResetReply> _resetCache = [];
    private OutgoingReset? _outgoingReset;
    private PeerReset? _peerOutgoingReset, _peerIncomingReset;

    public bool SupportsStreamReset { get { lock (_gate) return _options.EnableStreamReset && _peerStreamReset; } }

    /// <summary>Resets selected outgoing streams (empty means all). Cancellation stops waiting; an admitted wire request continues until resolved.</summary>
    public async Task ResetOutgoingStreamsAsync(ReadOnlyMemory<ushort> streamIds, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        ushort[] streams;
        lock (_gate)
        {
            RequireReady();
            if (!SupportsStreamReset) throw new NotSupportedException("The peer did not negotiate SCTP stream reset.");
            if (streamIds.Length > _outgoingStreams) throw new ArgumentOutOfRangeException(nameof(streamIds));
            streams = streamIds.IsEmpty ? AllStreams(_outgoingStreams) : streamIds.ToArray();
            if (streams.Any(stream => stream >= _outgoingStreams) || streams.Distinct().Count() != streams.Length ||
                (!streamIds.IsEmpty && 32 + ((2 * streams.Length + 3) & ~3) > _options.MaximumPacketSize))
                throw new ArgumentOutOfRangeException(nameof(streamIds));
        }
        while (true)
        {
            Task wait; OutgoingReset? reset = null;
            lock (_gate)
            {
                RequireReady(); linked.Token.ThrowIfCancellationRequested();
                if (_closing) throw new InvalidOperationException("SCTP is shutting down.");
                if (_outgoingReset == null && _peerIncomingReset == null && _resetEventReservations < _options.MaximumQueuedResetEvents)
                    reset = BeginOutgoingReset(streams, streamIds.IsEmpty, unchecked(_nextPeerResetSequence - 1));
                wait = _resetAdmission.Task;
            }
            if (reset != null)
            {
                try { await reset.Done.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (reset.Done.Task.IsCompletedSuccessfully && !cancellationToken.IsCancellationRequested)
                {
                    // SCTP's terminal cancellation can win the asynchronously scheduled wait
                    // after a validated successful response. Preserve that confirmed result,
                    // while caller cancellation and unconfirmed/refused resets still fail.
                }
                return;
            }
            await wait.WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }
    private static ushort[] AllStreams(ushort count)
    { var streams = new ushort[count]; for (ushort i = 0; i < count; i++) streams[i] = i; return streams; }
    private void PulseResetAdmission()
    { _resetAdmission.TrySetResult(); _resetAdmission = NewSignal(); }
    private void PulseSendAdmission()
    { _space.TrySetResult(); _space = NewSignal(); Wake(); }

    private OutgoingReset BeginOutgoingReset(ushort[] streams, bool all, uint responseSequence)
    {
        if (unchecked(_nextResetSequence - _initialTsn) >= int.MaxValue) throw new InvalidOperationException("Reset serial-number lifetime exhausted; establish a fresh association.");
        var sequence = _nextResetSequence++;
        var parameter = new byte[16 + (all ? 0 : 2 * streams.Length)];
        SctpWire.U16(parameter, 13); SctpWire.U16(parameter.AsSpan(2), (ushort)parameter.Length);
        SctpWire.U32(parameter.AsSpan(4), sequence); SctpWire.U32(parameter.AsSpan(8), responseSequence);
        SctpWire.U32(parameter.AsSpan(12), unchecked(_nextTsn - 1));
        if (!all) for (var i = 0; i < streams.Length; i++) SctpWire.U16(parameter.AsSpan(16 + 2 * i), streams[i]);
        var reset = new OutgoingReset(sequence, streams, SctpWire.Chunk(130, 0, parameter), _rto);
        _outgoingReset = reset; _resetEventReservations++;
        foreach (var stream in streams) _resettingStreams.Add(stream);
        QueueControl(reset.Chunk); PulseSendAdmission(); return reset;
    }
    private void RetryResetFlight()
    {
        if (_outgoingReset is not { } reset || Stopwatch.GetElapsedTime(reset.SentAt) < reset.Rto) return;
        if (!reset.InProgress && ++reset.Errors > _options.MaximumRetransmissions)
            throw new TimeoutException("SCTP stream-reset response limit reached.");
        QueueControl(reset.Chunk); reset.SentAt = Stopwatch.GetTimestamp();
        reset.Rto = TimeSpan.FromSeconds(Math.Min(60, 2 * reset.Rto.TotalSeconds)); _retransmissions++;
    }

    private void ProcessReconfiguration(ReadOnlySpan<byte> body)
    {
        // Preflight both TLVs before applying either. Arrays are bounded by the
        // negotiated stream ceiling, not attacker-provided parameter lengths.
        var parameters = new List<ResetParameter>(2);
        while (!body.IsEmpty)
        {
            if (body.Length < 4 || parameters.Count == 2) { Reject(); return; }
            var type = SctpWire.U16(body); var length = SctpWire.U16(body[2..]); var padded = (length + 3) & ~3;
            if (length < 4 || length > body.Length || (padded > body.Length && length != body.Length)) { Reject(); return; }
            var value = body[..length]; ushort[] streams = [];
            switch (type)
            {
                case 13 or 14:
                    var prefix = type == 13 ? 16 : 8;
                    var limit = type == 13 ? _incomingStreams : _outgoingStreams;
                    if (length < prefix || (length - prefix) % 2 != 0 || (length - prefix) / 2 > limit) { Reject(); return; }
                    streams = length == prefix ? AllStreams(limit) : new ushort[(length - prefix) / 2];
                    if (length != prefix) for (var i = 0; i < streams.Length; i++) streams[i] = SctpWire.U16(value[(prefix + 2 * i)..]);
                    if (streams.Any(stream => stream >= limit) || streams.Distinct().Count() != streams.Length) { Reject(); return; }
                    break;
                case 15: if (length != 8) { Reject(); return; } break;
                case 16: if (length is not (12 or 20) || SctpWire.U32(value[8..]) > 6) { Reject(); return; } break;
                case 17 or 18: if (length != 12) { Reject(); return; } break;
                default: Reject(); return;
            }
            parameters.Add(new(type, value.ToArray(), streams));
            body = padded >= body.Length ? [] : body[padded..];
        }
        if (parameters.Count == 0 || (parameters.Count == 2 && !ValidResetPair(parameters[0].Type, parameters[1].Type)))
        { Reject(); return; }
        foreach (var parameter in parameters)
        {
            if (parameter.Type == 16) { ProcessResetResponse(parameter.Bytes); continue; }
            _sackNeeded = true;
            var sequence = SctpWire.U32(parameter.Bytes.AsSpan(4));
            if (_resetCache.TryGetValue(sequence, out var cached))
            {
                if (!cached.Request.AsSpan().SequenceEqual(parameter.Bytes)) { Reject(); continue; }
                QueueControl(cached.Chunk); continue;
            }
            if (sequence != _nextPeerResetSequence)
            { Reject(); QueueControl(ResetResponse(sequence, 5)); continue; }
            _nextPeerResetSequence++;
            if (parameter.Type == 13) ProcessPeerOutgoingReset(sequence, parameter);
            else if (parameter.Type == 14) ProcessPeerIncomingReset(sequence, parameter);
            else { Reject(); CacheResetReply(sequence, parameter.Bytes, ResetResponse(sequence, 2)); }
        }
    }
    private static bool ValidResetPair(ushort first, ushort second) =>
        (first == 13 && second == 14) || (first == 14 && second == 13) ||
        (first == 17 && second == 18) || (first == 18 && second == 17) ||
        (first == 16 && second is 13 or 16) || (first == 13 && second == 16);

    private void ProcessResetResponse(byte[] parameter)
    {
        if (_outgoingReset is not { } reset || SctpWire.U32(parameter.AsSpan(4)) != reset.Sequence) return;
        if (parameter.Length != 12) { Reject(); return; } // Optional TSNs apply only to association reset, not an SSN reset.
        var result = SctpWire.U32(parameter.AsSpan(8));
        if (result == 6)
        {
            reset.InProgress = true;
            reset.SentAt = Stopwatch.GetTimestamp(); reset.Rto = _rto; return;
        }
        _outgoingReset = null;
        foreach (var stream in reset.Streams) _resettingStreams.Remove(stream);
        if (result is 0 or 1)
        {
            foreach (var stream in reset.Streams) _sendSequences.Remove(stream);
            EnqueueResetEvent(reset.Streams, true); reset.Done.TrySetResult();
        }
        else { _resetEventReservations--; reset.Done.TrySetException(new IOException($"Peer refused SCTP stream reset (result {result}).")); }
        PulseResetAdmission(); PulseSendAdmission(); StartPendingPeerIncomingReset();
    }
    private void ProcessPeerOutgoingReset(uint sequence, ResetParameter parameter)
    {
        // RFC 6525 section 4.1 makes this an implicit response to an
        // Incoming SSN Reset (14), which we never originate. Our requests
        // are Outgoing SSN Reset (13): only an explicit successful result
        // proves their SSNs can reset. A reciprocal 13 can race a lost result.
        var barrier = SctpWire.U32(parameter.Bytes.AsSpan(12));
        if (_peerOutgoingReset != null)
        { CacheResetReply(sequence, parameter.Bytes, ResetResponse(sequence, 4)); return; }
        if (_resetEventReservations >= _options.MaximumQueuedResetEvents ||
            (SctpWire.After(barrier, _receiveTsn) && unchecked(barrier - _receiveTsn) > MaximumChunks))
        { Reject(); CacheResetReply(sequence, parameter.Bytes, ResetResponse(sequence, 2)); return; }
        _resetEventReservations++;
        _peerOutgoingReset = new(sequence, parameter.Bytes, parameter.Streams, barrier);
        CompletePeerReset();
        if (_peerOutgoingReset != null) CacheResetReply(sequence, parameter.Bytes, ResetResponse(sequence, 6));
    }
    private void CompletePeerReset()
    {
        if (_peerOutgoingReset is not { } reset || SctpWire.After(reset.Barrier, _receiveTsn)) return;
        FlushDelivery();
        // A cumulative TSN alone does not prove that all preceding complete
        // messages have reached the ordered receive queue under backpressure.
        if (_fragments.Any(item => reset.Streams.Contains(item.Value.Stream) && !SctpWire.After(item.Key, reset.Barrier)) ||
            _unorderedPending.Any(item => reset.Streams.Contains(item.StreamId)) ||
            reset.Streams.Any(stream => _ordered.TryGetValue(stream, out var queue) && queue.Count != 0)) return;
        foreach (var stream in reset.Streams)
        { _receiveSequences.Remove(stream); _forwardSequences.Remove(stream); _ordered.Remove(stream); }
        _peerOutgoingReset = null;
        EnqueueResetEvent(reset.Streams, false);
        CacheResetReply(reset.Sequence, reset.Request, ResetResponse(reset.Sequence, 1));
        AssembleAvailable();
    }
    private void ProcessPeerIncomingReset(uint sequence, ResetParameter parameter)
    {
        if (_outgoingReset is { } active && parameter.Streams.All(active.Streams.Contains))
        { CacheResetReply(sequence, parameter.Bytes, ResetResponse(sequence, 0)); return; }
        if (_peerIncomingReset != null || (parameter.Bytes.Length != 8 && 32 + ((2 * parameter.Streams.Length + 3) & ~3) > _options.MaximumPacketSize))
        { CacheResetReply(sequence, parameter.Bytes, ResetResponse(sequence, 2)); return; }
        _peerIncomingReset = new(sequence, parameter.Bytes, parameter.Streams, 0);
        StartPendingPeerIncomingReset();
        if (_peerIncomingReset != null) CacheResetReply(sequence, parameter.Bytes, ResetResponse(sequence, 6));
    }
    private void StartPendingPeerIncomingReset()
    {
        if (_peerIncomingReset is not { } reset || _outgoingReset != null || _resetEventReservations >= _options.MaximumQueuedResetEvents || !_ready || _closing) return;
        _peerIncomingReset = null;
        var outbound = BeginOutgoingReset(reset.Streams, reset.Request.Length == 8, reset.Sequence);
        CacheResetReply(reset.Sequence, reset.Request, outbound.Chunk);
    }
    private void EnqueueResetEvent(ushort[] streams, bool outgoing)
    {
        if (!_messages.Writer.TryWrite(new SctpStreamReset(streams, outgoing))) throw new IOException("SCTP reset notification accounting failed.");
    }
    private void CacheResetReply(uint sequence, byte[] request, byte[] chunk)
    {
        if (!_resetCache.ContainsKey(sequence) && _resetCache.Count == 2) _resetCache.Remove(_resetCache.Keys.First());
        _resetCache[sequence] = new(request, chunk); QueueControl(chunk);
    }
    private static byte[] ResetResponse(uint sequence, uint result)
    {
        var parameter = new byte[12]; SctpWire.U16(parameter, 16); SctpWire.U16(parameter.AsSpan(2), 12);
        SctpWire.U32(parameter.AsSpan(4), sequence); SctpWire.U32(parameter.AsSpan(8), result);
        return SctpWire.Chunk(130, 0, parameter);
    }
    private sealed record ResetParameter(ushort Type, byte[] Bytes, ushort[] Streams);
    private sealed record ResetReply(byte[] Request, byte[] Chunk);
    private sealed record PeerReset(uint Sequence, byte[] Request, ushort[] Streams, uint Barrier);
    private sealed class OutgoingReset(uint sequence, ushort[] streams, byte[] chunk, TimeSpan rto)
    {
        internal readonly uint Sequence = sequence;
        internal readonly ushort[] Streams = streams;
        internal readonly byte[] Chunk = chunk;
        internal readonly TaskCompletionSource Done = NewSignal();
        internal long SentAt = Stopwatch.GetTimestamp();
        internal TimeSpan Rto = rto;
        internal int Errors;
        internal bool InProgress;
    }
}
