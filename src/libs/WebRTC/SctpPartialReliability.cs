using System.Diagnostics;

namespace tryAGI.WebRTC;

/// <summary>One immutable per-message PR-SCTP policy. Timed units are milliseconds; retransmission units exclude the first send.</summary>
public sealed record SctpReliability(DataChannelReliability Mode, uint Parameter)
{
    public static SctpReliability Reliable { get; } = new(DataChannelReliability.Reliable, 0);
}

public sealed partial class SctpAssociation
{
    private bool _peerPartialReliability;
    private long _abandonedMessages, _forwardAt;
    private uint _forwardPoint;
    private byte[]? _forwardFlight;
    private TimeSpan _forwardRto;
    private int _forwardRetries;
    private readonly Dictionary<ushort, ushort> _forwardSequences = [];

    public bool SupportsPartialReliability { get { lock (_gate) return _options.EnablePartialReliability && _peerPartialReliability; } }

    private static void ValidateReliability(SctpReliability policy)
    {
        if (!Enum.IsDefined(policy.Mode) || (policy.Mode == DataChannelReliability.Reliable && policy.Parameter != 0))
            throw new ArgumentOutOfRangeException(nameof(policy));
    }
    private static bool Expired(SctpReliability policy, long created) => policy.Mode == DataChannelReliability.Timed &&
        Stopwatch.GetElapsedTime(created).TotalMilliseconds >= policy.Parameter;

    private sealed class OutboundMessage(ushort stream, ushort sequence, bool unordered, SctpReliability policy, long created)
    {
        internal readonly ushort Stream = stream, Sequence = sequence;
        internal readonly bool Unordered = unordered;
        internal readonly SctpReliability Policy = policy;
        internal readonly long CreatedAt = created;
        internal bool Abandoned;
    }

    private void ExpireMessages()
    {
        foreach (var message in _outbound.Values.Select(value => value.Message).Distinct())
            if (!message.Abandoned && Expired(message.Policy, message.CreatedAt)) Abandon(message);
    }
    private void Abandon(OutboundMessage message)
    {
        if (message.Abandoned) return;
        message.Abandoned = true; _abandonedMessages++;
        foreach (var (tsn, item) in _outbound)
        {
            if (!ReferenceEquals(item.Message, message)) continue;
            _sendBytes -= item.Size;
            // Keep bounded TSN/message metadata until the peer confirms the forward
            // point, but release abandoned payload storage immediately.
            item.Chunk = []; item.Retransmit = false;
            if (_timeoutRecovery && tsn == _timeoutRecoveryTsn) _timeoutRecovery = false;
        }
        _space.TrySetResult(); _space = NewSignal(); Wake();
    }

    private void UpdateForwardFlight()
    {
        if (!_ready || !SupportsPartialReliability || _shutdownAckSent || _finishAfterFlush) return;
        var point = _acknowledgedTsn;
        var sequences = new Dictionary<ushort, ushort>();
        var capacity = (_options.MaximumPacketSize - 20) / 4;
        while (_outbound.TryGetValue(unchecked(point + 1), out var item) && item.Message.Abandoned)
        {
            var message = item.Message;
            if (!message.Unordered)
            {
                if (!sequences.ContainsKey(message.Stream) && sequences.Count == capacity) break;
                sequences[message.Stream] = message.Sequence;
            }
            point++;
        }
        if (point == _acknowledgedTsn || (_forwardFlight != null && point == _forwardPoint)) return;
        var body = new byte[4 + 4 * sequences.Count]; SctpWire.U32(body, point);
        var offset = 4;
        foreach (var (stream, sequence) in sequences)
        { SctpWire.U16(body.AsSpan(offset), stream); SctpWire.U16(body.AsSpan(offset + 2), sequence); offset += 4; }
        _forwardPoint = point; _forwardFlight = SctpWire.Chunk(192, 0, body);
        _forwardAt = Stopwatch.GetTimestamp(); _forwardRto = _rto; _forwardRetries = 0;
        if (SctpWire.After(point, _highestSentTsn)) _highestSentTsn = point;
        QueueControl(_forwardFlight);
    }
    private void RetryForwardFlight()
    {
        if (_forwardFlight == null || Stopwatch.GetElapsedTime(_forwardAt) < _forwardRto) return;
        if (++_forwardRetries > _options.MaximumRetransmissions) throw new TimeoutException("FORWARD-TSN acknowledgment limit reached.");
        QueueControl(_forwardFlight); _forwardAt = Stopwatch.GetTimestamp();
        _forwardRto = TimeSpan.FromSeconds(Math.Min(60, 2 * _forwardRto.TotalSeconds));
        _retransmissions++;
    }

    private void ProcessForwardTsn(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4 || (body.Length - 4) % 4 != 0 || (body.Length - 4) / 4 > _incomingStreams)
        { Reject(); return; }
        var point = SctpWire.U32(body); _sackNeeded = true;
        if (!SctpWire.After(point, _receiveTsn)) return;
        if (unchecked(point - _receiveTsn) > MaximumChunks) { Reject(); return; }
        var sequences = new Dictionary<ushort, ushort>();
        for (var offset = 4; offset < body.Length; offset += 4)
        {
            var stream = SctpWire.U16(body[offset..]); var sequence = SctpWire.U16(body[(offset + 2)..]);
            var expected = _receiveSequences.GetValueOrDefault(stream);
            if (stream >= _incomingStreams || !sequences.TryAdd(stream, sequence) ||
                (SctpWire.After(sequence, unchecked((ushort)(expected - 1))) && unchecked((ushort)(sequence - expected)) >= MaximumChunks))
            { Reject(); return; }
        }
        var previous = _receiveTsn; _receiveTsn = point;
        _gaps.RemoveWhere(tsn => !SctpWire.After(tsn, point));
        while (_gaps.Remove(unchecked(_receiveTsn + 1))) _receiveTsn++;
        // Preserve complete stranded messages before releasing incomplete fragments.
        AssembleAvailable();
        var discard = false;
        foreach (var tsn in _fragments.Keys.OrderBy(value => unchecked((int)(value - previous))).ToArray())
        {
            var fragment = _fragments[tsn];
            if ((fragment.Flags & 2) != 0 || !discard) discard = !SctpWire.After(tsn, point);
            if (!discard) continue;
            _receiveBytes -= fragment.Data.Length; _fragments.Remove(tsn); _gaps.Remove(tsn);
        }
        foreach (var (stream, sequence) in sequences)
        {
            var expected = _receiveSequences.GetValueOrDefault(stream);
            if (!SctpWire.After(sequence, unchecked((ushort)(expected - 1)))) continue;
            if (!_forwardSequences.TryGetValue(stream, out var old) || SctpWire.After(sequence, old)) _forwardSequences[stream] = sequence;
            if (!_ordered.ContainsKey(stream)) _ordered[stream] = [];
        }
        FlushDelivery(); CompletePeerReset();
    }
}
