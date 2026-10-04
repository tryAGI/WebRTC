namespace tryAGI.WebRTC;

/// <summary>Single endpoint's AVPF send schedule with an explicit, already allocated RTCP bandwidth share.
/// Early feedback requires caller-confirmed two-participant topology. This is not a multicast member estimator or a hard byte-rate limiter.</summary>
public sealed class RtcpTransmissionSchedule
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly double _bandwidth;
    private readonly long _started;
    private bool _pointToPoint;
    private double _average, _previous, _next;
    private bool _initial = true, _allowEarly = true;

    /// <param name="bandwidthBytesPerSecond">This endpoint's allocated RTCP share, including transport overhead; not the full media/session bandwidth.</param>
    /// <param name="initialOnWireBytes">Estimated initial datagram size, including lower-layer overhead.</param>
    /// <param name="pointToPoint">True only when signaling guarantees two participants. Unicast addresses or multiple SSRCs do not establish participant count.</param>
    /// <param name="timeProvider">Monotonic clock. Callbacks must not reenter this instance.</param>
    /// <param name="random">Uniform sample in [0, 1], or the shared platform random source. Callbacks must not reenter this instance.</param>
    public RtcpTransmissionSchedule(double bandwidthBytesPerSecond, int initialOnWireBytes, bool pointToPoint,
        TimeProvider? timeProvider = null, Func<double>? random = null)
    {
        if (!double.IsFinite(bandwidthBytesPerSecond) || bandwidthBytesPerSecond is < 1 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(bandwidthBytesPerSecond));
        if (initialOnWireBytes is < 1 or > 1500) throw new ArgumentOutOfRangeException(nameof(initialOnWireBytes));
        _time = timeProvider ?? TimeProvider.System; _random = random ?? Random.Shared.NextDouble;
        _bandwidth = bandwidthBytesPerSecond; _average = initialOnWireBytes; _pointToPoint = pointToPoint;
        _started = _time.GetTimestamp(); _next = Interval(_average, _initial, _pointToPoint);
    }
    private double Now => _time.GetElapsedTime(_started).TotalSeconds;
    private double Interval(double average, bool initial, bool pointToPoint)
    {
        var sample = _random();
        if (!double.IsFinite(sample) || sample is < 0 or > 1) throw new InvalidOperationException("Invalid RTCP random sample.");
        var minimum = initial && !pointToPoint ? 1 : 0;
        return Math.Max(minimum, average / _bandwidth) * (0.5 + sample) / 1.21828;
    }
    public bool EarlyFeedbackEnabled { get { lock (_gate) return _pointToPoint; } }
    /// <summary>Irreversibly disables early feedback after topology evidence contradicts the caller's initial guarantee.</summary>
    public void DisableEarlyFeedback()
    {
        lock (_gate)
        {
            if (!_pointToPoint) return;
            var next = _initial ? Math.Max(_next, Now + Interval(_average, true, false)) : _next;
            _pointToPoint = false; _allowEarly = false; _next = next;
        }
    }
    /// <summary>Updates average on-wire size using admitted sent/received RTCP. Authentication and source admission are caller-owned.</summary>
    public void ObserveSize(int onWireBytes)
    {
        ValidateSize(onWireBytes);
        lock (_gate) _average = (15 * _average + onWireBytes) / 16;
    }
    public TimeSpan Delay(bool hasFeedback, out bool early)
    {
        lock (_gate)
        {
            var now = Now; early = hasFeedback && _pointToPoint && _allowEarly && now < _next;
            return early ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, _next - now));
        }
    }
    /// <summary>Records a successful transmission. Pass early from Delay; a send delayed past the regular deadline becomes regular.
    /// Only one early transmission is allowed between regular transmissions; it postpones one regular slot.</summary>
    public void Sent(int onWireBytes, bool early)
    {
        ValidateSize(onWireBytes);
        lock (_gate)
        {
            if (early && (!_pointToPoint || !_allowEarly)) throw new InvalidOperationException("Early RTCP was not admitted.");
            var now = Now;
            if (!early && now < _next) throw new InvalidOperationException("Regular RTCP is not due.");
            var average = (15 * _average + onWireBytes) / 16;
            if (early && now < _next)
            {
                var interval = _next - _previous; _previous += interval; _next = _previous + interval; _allowEarly = false;
            }
            else
            {
                var interval = Interval(average, false, _pointToPoint);
                _previous = now; _next = _previous + interval; _allowEarly = true;
            }
            _average = average; _initial = false;
        }
    }
    private static void ValidateSize(int bytes)
    { if (bytes is < 1 or > 1500) throw new ArgumentOutOfRangeException(nameof(bytes)); }
}
