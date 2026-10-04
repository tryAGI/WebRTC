namespace tryAGI.WebRTC;

/// <summary>RFC 3550 reception report statistics. Caller supplies authorized packets and monotonic arrival times.</summary>
public sealed class RtpReceptionTracker
{
    private readonly object _gate = new();
    private readonly int _clock;
    private bool _started, _ready;
    private ushort _last, _candidate;
    private bool _restartCandidate;
    private uint _candidateTimestamp;
    private TimeSpan _candidateArrival;
    private long _highest, _base, _received, _priorExpected, _priorReceived;
    private TimeSpan _arrival, _observedAt;
    private uint _timestamp;
    private double _jitter;
    private long _resetEpoch;
    private PacketReason _lastReason;
    public RtpReceptionSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var expected = _highest - _base + 1;
            var intervalExpected = expected - _priorExpected;
            var intervalLoss = intervalExpected - (_received - _priorReceived);
            var fraction = !_ready || intervalExpected <= 0 || intervalLoss <= 0 ? (byte)0 : (byte)Math.Min(255, 256.0 * intervalLoss / intervalExpected);
            return new(_clock, _resetEpoch, _observedAt, _ready, fraction, _ready ? (int)Math.Clamp(expected - _received, -8388608, 8388607) : 0,
                _ready ? unchecked((uint)_highest) : 0, (uint)Math.Clamp(_jitter, 0, uint.MaxValue), _received, _lastReason);
        }
    }
    public uint Source { get; }
    public bool IsReady { get { lock (_gate) return _ready; } }
    public RtpReceptionTracker(uint source, int clockRate)
    {
        if (clockRate is < 1 or > 192000) throw new ArgumentOutOfRangeException(nameof(clockRate));
        Source = source; _clock = clockRate;
    }
    public bool Observe(ushort sequence, uint timestamp, TimeSpan arrival)
    {
        lock (_gate)
        {
            if (arrival < TimeSpan.Zero || _started && arrival < _observedAt) throw new ArgumentOutOfRangeException(nameof(arrival));
            _observedAt = arrival;
            if (!_started) { _started = true; _last = sequence; _timestamp = timestamp; _arrival = arrival; return false; }
            var delta = unchecked((ushort)(sequence - _last));
            _lastReason = delta == 0 ? PacketReason.Duplicate : delta > 65536 - 100 ? PacketReason.Reordered : PacketReason.None;
            if (!_ready)
            {
                if (delta != 1) { _last = sequence; _timestamp = timestamp; _arrival = arrival; return false; }
                Restart(_last, sequence); _ready = true;
            }
            else if (delta < 3000)
            { _highest += delta; _last = sequence; _received++; _restartCandidate = false; }
            else if (delta <= 65536 - 100)
            {
                if (_restartCandidate && sequence == unchecked((ushort)(_candidate + 1)))
                { _arrival = _candidateArrival; _timestamp = _candidateTimestamp; Restart(_candidate, sequence); }
                else { _candidate = sequence; _candidateTimestamp = timestamp; _candidateArrival = arrival; _restartCandidate = true; return false; }
            }
            else _received++; // Bounded misordering or duplication; cumulative loss may be negative.
            var transitChange = (arrival - _arrival).TotalSeconds * _clock - unchecked((int)(timestamp - _timestamp));
            _jitter += (Math.Abs(transitChange) - _jitter) / 16;
            _arrival = arrival; _timestamp = timestamp; return true;
        }
    }
    private void Restart(ushort first, ushort second)
    {
        _resetEpoch++;
        _base = first; _highest = second < first ? 65536L + second : second; _last = second;
        _received = 2; _priorExpected = _priorReceived = 0; _restartCandidate = false; _jitter = 0;
    }
    /// <summary>Creates and advances one reporting interval, after two consecutive packets validate sequence state.</summary>
    public RtcpReceptionReport? CreateReport(uint lastSenderReport = 0, TimeSpan delaySinceSenderReport = default)
    {
        if (delaySinceSenderReport < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delaySinceSenderReport));
        lock (_gate)
        {
            if (!_ready) return null;
            var expected = _highest - _base + 1; var intervalExpected = expected - _priorExpected;
            var intervalLoss = intervalExpected - (_received - _priorReceived);
            var fraction = intervalExpected <= 0 || intervalLoss <= 0 ? (byte)0 : (byte)Math.Min(255, 256.0 * intervalLoss / intervalExpected);
            _priorExpected = expected; _priorReceived = _received;
            return new(Source, fraction, (int)Math.Clamp(expected - _received, -8388608, 8388607), unchecked((uint)_highest),
                (uint)Math.Clamp(_jitter, 0, uint.MaxValue), lastSenderReport,
                lastSenderReport == 0 ? 0 : (uint)Math.Min(uint.MaxValue, delaySinceSenderReport.TotalSeconds * 65536));
        }
    }
}

public readonly record struct RtpReceptionSnapshot(int ClockRate, long ResetEpoch, TimeSpan ObservedAt, bool Ready,
    byte FractionLost, int CumulativeLost, uint HighestSequence, uint JitterRtpTicks, long ReceivedPackets, PacketReason LastReason);

public static class RtcpClock
{
    /// <summary>NTP seconds/fraction wire fields. Seconds wrap at an NTP era boundary; no era is inferred when parsing.</summary>
    public static ulong ToNtpTimestamp(DateTimeOffset time)
    {
        var ticks = time.UtcTicks - new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(time));
        return ((ulong)unchecked((uint)(ticks / TimeSpan.TicksPerSecond)) << 32) |
            (ulong)(ticks % TimeSpan.TicksPerSecond) * 0x100000000UL / TimeSpan.TicksPerSecond;
    }
    public static uint Compact(ulong timestamp) => unchecked((uint)(timestamp >> 16));
}
