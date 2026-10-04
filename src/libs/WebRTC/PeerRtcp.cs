using System.Diagnostics;
using System.Threading.Channels;
using System.Text;

namespace tryAGI.WebRTC;

public sealed record PeerRtcpOptions
{
    public int SessionBandwidthBitsPerSecond { get; init; } = 128000;
    public int MaximumControlBytesPerSecond { get; init; } = 4096;
    public int MaximumReceptionSources { get; init; } = 31;
    /// <summary>Set only when signaling guarantees two participants. Enables RFC 4585 immediate early feedback.</summary>
    public bool PointToPoint { get; init; }
    public TimeSpan MinimumPictureLossInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public int MaximumPictureLossAttempts { get; init; } = 3;
}
public sealed record VideoKeyFrameRequest(uint SenderSource, uint MediaSource);
public sealed record PeerRtcpDiagnostics(int ReceptionSources, long SentPackets, long ReceivedPackets, long RejectedPackets,
    long SentPictureLoss, long ReceivedPictureLoss, long SuppressedPictureLoss, TimeSpan? RoundTripTime, bool EarlyFeedbackEnabled);

internal sealed class PeerRtcp
{
    private sealed class Reception(uint source, int clock)
    {
        internal readonly RtpReceptionTracker Tracker = new(source, clock);
        internal readonly bool Video = clock == 90000;
        internal RtcpSenderReport? SenderReport;
        internal long ReceivedAt;
    }
    private sealed class Sender(uint source, int clock)
    {
        internal readonly uint Source = source;
        internal readonly int Clock = clock;
        internal uint Timestamp, Count, Octets;
        internal long SentAt;
    }
    private sealed class Refresh(long revision)
    {
        internal long Revision = revision;
        internal int Attempts;
        internal long SentAt;
    }
    private sealed record Raw(byte[] Data, CancellationToken Token)
    {
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Channel<Raw> _raw = Channel.CreateBounded<Raw>(new BoundedChannelOptions(8) { SingleReader = true });
    private bool _closed;
    private Raw? _activeRaw;
    private readonly object _gate = new();
    private readonly SdpNegotiatedSession _session;
    private readonly PeerRtcpOptions _options;
    private readonly PeerVideo _video;
    private readonly string _cname;
    private readonly Sender _audioSender, _videoSender;
    private readonly Dictionary<uint, Reception> _received = [];
    private readonly Dictionary<uint, Refresh> _refresh = [];
    private readonly HashSet<uint> _remoteAudio, _remoteVideo, _controlSenders = [];
    private readonly Dictionary<uint, string> _remoteNames = [];
    private readonly Dictionary<(uint Source, uint Compact), long> _sentReports = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<VideoKeyFrameRequest> _keyFrames = Channel.CreateBounded<VideoKeyFrameRequest>(new BoundedChannelOptions(8)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly RtcpTransmissionSchedule _schedule;
    private readonly int _maximumBytes;
    private readonly double _budget;
    private double _tokens;
    private long _budgetAt = Stopwatch.GetTimestamp();
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _sent, _read, _rejected, _pliSent, _pliReceived, _pliSuppressed, _lastPliAt;
    private TimeSpan? _rtt;
    private int _reportCursor;
    internal PeerRtcp(SdpNegotiatedSession session, PeerVideo video, uint audioSource, uint videoSource, string cname, PeerRtcpOptions options, int mtu)
    {
        _session = session; _options = options; _video = video; _cname = cname;
        _audioSender = new(audioSource, 48000); _videoSender = new(videoSource, 90000);
        _remoteAudio = session.RemoteAudio?.Sources.ToHashSet() ?? []; _remoteVideo = session.RemoteVideo?.Sources.ToHashSet() ?? [];
        _maximumBytes = Math.Min(1200, mtu - 20);
        var budget = Math.Min(options.MaximumControlBytesPerSecond, options.SessionBandwidthBitsPerSecond * 0.025 / 8);
        _budget = budget; _tokens = _maximumBytes + 128;
        _schedule = new(budget, 256, options.PointToPoint);
    }
    internal static void Validate(PeerRtcpOptions options, int mtu = 1200)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (mtu is < 256 or > 1200 || options.SessionBandwidthBitsPerSecond is < 16000 or > 100000000 || options.MaximumControlBytesPerSecond is < 64 or > 32768 ||
            options.MaximumReceptionSources is < 1 or > 31 || options.MaximumPictureLossAttempts is < 1 or > 8 ||
            options.MinimumPictureLossInterval < TimeSpan.FromMilliseconds(100) || options.MinimumPictureLossInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    internal void Wake() => _wake.Writer.TryWrite(true);
    internal void SentRtp(bool video, uint timestamp, int bytes)
    {
        lock (_gate)
        {
            var sender = video ? _videoSender : _audioSender;
            sender.Timestamp = timestamp; sender.SentAt = Stopwatch.GetTimestamp(); sender.Count = unchecked(sender.Count + 1); sender.Octets = unchecked(sender.Octets + (uint)bytes);
        }
    }
    internal void ReceivedRtp(uint source, ushort sequence, uint timestamp, bool video)
    {
        lock (_gate)
        {
            if (!_received.TryGetValue(source, out var reception))
            {
                if (_received.Count == _options.MaximumReceptionSources) return;
                reception = new(source, video ? 90000 : 48000); _received.Add(source, reception);
            }
            _controlSenders.Remove(source);
            reception.Tracker.Observe(sequence, timestamp, Stopwatch.GetElapsedTime(_started));
        }
    }
    internal IAsyncEnumerable<VideoKeyFrameRequest> KeyFrameRequests(CancellationToken ct) => _keyFrames.Reader.ReadAllAsync(ct);
    internal void RequestPictureLoss(uint source)
    {
        if (!_session.CanReceiveVideo || !_session.VideoPictureLoss) throw new NotSupportedException("Picture-loss feedback was not negotiated.");
        if (!_video.HasSource(source) && !_remoteVideo.Contains(source)) throw new ArgumentException("Video source is not authorized.", nameof(source));
        _video.RequestRefresh(source); Wake();
    }
    internal bool Read(byte[] data)
    {
        lock (_gate)
        {
            if (_closed || !RtcpPackets.TryParse(data, out var packets, out var compound)) return Reject();
            var senders = packets.Where(p => p is RtcpSenderReport or RtcpReceiverReport or RtcpPictureLossIndication).Select(RtcpPackets.Sender).Distinct().ToArray();
            if (senders.Length == 0 || senders.Any(Local)) return Reject();
            var names = packets.OfType<RtcpSourceDescription>().SelectMany(s => s.Chunks).Where(c => c.CanonicalName != null).ToArray();
            if (names.Any(n => Local(n.Source) || _remoteNames.TryGetValue(n.Source, out var prior) && prior != n.CanonicalName) ||
                _remoteNames.Keys.Concat(names.Select(n => n.Source)).Distinct().Count() > 64) return Reject();
            if (!compound)
            {
                var allowed = packets.All(p => p switch
                {
                    RtcpPictureLossIndication => _session.VideoReducedSizeRtcp,
                    RtcpSenderReport sr => ReducedSizeFor(sr.SenderSource),
                    RtcpReceiverReport rr => ReducedSizeFor(rr.SenderSource),
                    RtcpOpaquePacket => _session.AudioReducedSizeRtcp || _session.VideoReducedSizeRtcp,
                    _ => false
                });
                if (!allowed) return Reject();
            }
            if (senders.Any(s => !_received.ContainsKey(s) && !_remoteAudio.Contains(s) && !_remoteVideo.Contains(s) && !_controlSenders.Contains(s)) &&
                _controlSenders.Union(senders.Where(s => !_received.ContainsKey(s) && !_remoteAudio.Contains(s) && !_remoteVideo.Contains(s))).Count() > 8) return Reject();
            var feedback = packets.OfType<RtcpPictureLossIndication>().ToArray();
            if (feedback.Any(p => !_session.CanSendVideo || !_session.VideoPictureLoss || p.MediaSource != _videoSender.Source)) return Reject();
            foreach (var s in senders) if (!_received.ContainsKey(s) && !_remoteAudio.Contains(s) && !_remoteVideo.Contains(s)) _controlSenders.Add(s);
            foreach (var n in names) _remoteNames[n.Source] = n.CanonicalName!;
            if (_remoteNames.Values.Distinct(StringComparer.Ordinal).Take(2).Count() > 1) _schedule.DisableEarlyFeedback();
            var now = Stopwatch.GetTimestamp();
            foreach (var sr in packets.OfType<RtcpSenderReport>())
            {
                if (!_received.TryGetValue(sr.SenderSource, out var source) && (_remoteAudio.Contains(sr.SenderSource) || _remoteVideo.Contains(sr.SenderSource)) &&
                    _received.Count < _options.MaximumReceptionSources)
                { source = new(sr.SenderSource, _remoteVideo.Contains(sr.SenderSource) ? 90000 : 48000); _received.Add(sr.SenderSource, source); }
                if (source != null) { source.SenderReport = sr; source.ReceivedAt = now; }
            }
            foreach (var report in packets.SelectMany(p => p switch { RtcpSenderReport sr => sr.Reports, RtcpReceiverReport rr => rr.Reports, _ => Array.Empty<RtcpReceptionReport>() }))
                if (report.LastSenderReport != 0 && _sentReports.TryGetValue((report.Source, report.LastSenderReport), out var sentAt))
                {
                    var rtt = Stopwatch.GetElapsedTime(sentAt) - TimeSpan.FromSeconds(report.DelaySinceLastSenderReport / 65536.0);
                    if (Stopwatch.GetElapsedTime(sentAt) <= TimeSpan.FromMinutes(1) && rtt >= TimeSpan.Zero && rtt <= TimeSpan.FromMinutes(1)) _rtt = rtt;
                }
            foreach (var pli in feedback)
            {
                if (_lastPliAt != 0 && Stopwatch.GetElapsedTime(_lastPliAt) < _options.MinimumPictureLossInterval) { _pliSuppressed++; continue; }
                _lastPliAt = now; _pliReceived++; _keyFrames.Writer.TryWrite(new(pli.SenderSource, pli.MediaSource));
            }
            _read++; _schedule.ObserveSize(data.Length + 128); return true;
        }
    }
    private bool ReducedSizeFor(uint source)
    {
        if (_received.TryGetValue(source, out var reception)) return reception.Video ? _session.VideoReducedSizeRtcp : _session.AudioReducedSizeRtcp;
        if (_remoteVideo.Contains(source)) return _session.VideoReducedSizeRtcp;
        if (_remoteAudio.Contains(source)) return _session.AudioReducedSizeRtcp;
        return (_session.LocalAudio == null || _session.AudioReducedSizeRtcp) && (_session.LocalVideo == null || _session.VideoReducedSizeRtcp);
    }
    private bool Local(uint source) => source == _audioSender.Source || source == _videoSender.Source;
    private bool ActiveLocal(uint source) => source == _audioSender.Source && _session.LocalAudio != null || source == _videoSender.Source && _session.LocalVideo != null;
    private bool Reject() { _rejected++; return false; }
    internal bool ValidateOutgoing(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (data.Length > _maximumBytes || !RtcpPackets.TryParse(data, out var packets, out var compound)) return false;
            if (!packets.Any(p => p is RtcpSenderReport or RtcpReceiverReport)) return false;
            foreach (var p in packets)
            {
                if (p is RtcpSenderReport or RtcpReceiverReport)
                {
                    var source = RtcpPackets.Sender(p);
                    if (!ActiveLocal(source)) return false;
                    if (!compound && !(source == _audioSender.Source ? _session.AudioReducedSizeRtcp : _session.VideoReducedSizeRtcp)) return false;
                }
                else if (p is RtcpSourceDescription sdes)
                { if (sdes.Chunks.Any(c => !ActiveLocal(c.Source) || c.CanonicalName != _cname)) return false; }
                else return false; // PLI must use the owner's scheduled RequestPictureLoss API.
            }
            return true;
        }
    }
    internal async ValueTask SendExternalAsync(byte[] data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new Raw(data, ct);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_raw.Writer.TryWrite(request)) throw new InvalidOperationException("The bounded RTCP send queue is full.");
        }
        using var cancellation = ct.Register(static owner => ((PeerRtcp)owner!).Wake(), this);
        Wake();
        try { await request.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            // WaitAsync cancellation can dispose the registration before its wake callback runs.
            // Always notify the owner when a canceled caller leaves its queued request behind.
            if (ct.IsCancellationRequested) Wake();
        }
    }
    internal async Task RunAsync(DtlsSrtpTransport transport, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan wait; bool early = false; byte[]? data = null;
                List<RtcpSenderReport>? senderReports = null; List<uint>? feedback = null; Raw? request = null;
                long builtAt = 0;
                lock (_gate)
                {
                    while (_raw.Reader.TryPeek(out var cancelled) && cancelled.Token.IsCancellationRequested)
                    { _raw.Reader.TryRead(out _); cancelled.Completion.TrySetCanceled(cancelled.Token); }
                    var pending = PendingRefresh(); wait = _schedule.Delay(pending.Count != 0, out early);
                    if (wait <= TimeSpan.Zero)
                    {
                        var plan = Plan(pending); var bytes = plan.Bytes + 128;
                        wait = BudgetDelay(bytes);
                        if (wait <= TimeSpan.Zero)
                        {
                            _tokens -= bytes; builtAt = Stopwatch.GetTimestamp();
                            data = Build(plan.Reports, plan.Feedback, out senderReports); feedback = plan.Feedback;
                            if (data.Length != plan.Bytes) throw new IOException("RTCP packet plan changed during construction.");
                        }
                    }
                    else if (_raw.Reader.TryPeek(out var queued))
                    {
                        var rawDelay = BudgetDelay(queued.Data.Length + 128);
                        if (rawDelay <= TimeSpan.Zero)
                        {
                            _raw.Reader.TryRead(out request); _activeRaw = request;
                            _tokens -= queued.Data.Length + 128; data = queued.Data;
                        }
                        else if (rawDelay < wait) wait = rawDelay; // A queued large report cannot postpone the regular automatic deadline.
                    }
                    // Wake at a future throttle expiry, not repeatedly at an already eligible request.
                    if (data == null) wait = TimeSpan.FromSeconds(Math.Max(.001, Math.Min(wait.TotalSeconds, RefreshDelay().TotalSeconds)));
                }
                if (data != null)
                {
                    if (request != null)
                    {
                        using var send = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token);
                        try
                        {
                            await transport.SendRtcpAsync(data, send.Token).ConfigureAwait(false);
                            lock (_gate) { _sent++; _schedule.ObserveSize(data.Length + 128); RememberReports(data, Stopwatch.GetTimestamp()); }
                            request.Completion.TrySetResult();
                        }
                        catch (OperationCanceledException) when (request.Token.IsCancellationRequested && !ct.IsCancellationRequested)
                        { request.Completion.TrySetCanceled(request.Token); }
                        lock (_gate) _activeRaw = null;
                    }
                    else
                    {
                        await transport.SendRtcpAsync(data, ct).ConfigureAwait(false);
                        lock (_gate)
                        {
                            _sent++; _pliSent += feedback!.Count;
                            foreach (var source in feedback) { var state = _refresh[source]; state.SentAt = Stopwatch.GetTimestamp(); state.Attempts++; }
                            foreach (var sr in senderReports!) _sentReports[(sr.SenderSource, RtcpClock.Compact(sr.NtpTimestamp))] = builtAt;
                            PruneReports();
                            // Admission preceded the write. New topology received during that await cannot undo an emitted packet.
                            if (early && !_schedule.EarlyFeedbackEnabled || !early && _schedule.Delay(false, out _) > TimeSpan.Zero)
                                _schedule.ObserveSize(data.Length + 128);
                            else _schedule.Sent(data.Length + 128, early);
                        }
                    }
                    continue;
                }
                using var next = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timer = Task.Delay(wait, next.Token); var wake = _wake.Reader.ReadAsync(next.Token).AsTask();
                await Task.WhenAny(timer, wake).ConfigureAwait(false); next.Cancel();
                try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) when (next.IsCancellationRequested) { }
                try { await wake.ConfigureAwait(false); } catch (OperationCanceledException) when (next.IsCancellationRequested) { }
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Complete(); }
    }
    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return; _closed = true; _raw.Writer.TryComplete();
            var failure = new ObjectDisposedException("RTCP peer owner");
            _activeRaw?.Completion.TrySetException(failure); _activeRaw = null;
            while (_raw.Reader.TryRead(out var request))
            {
                if (request.Token.IsCancellationRequested) request.Completion.TrySetCanceled(request.Token);
                else request.Completion.TrySetException(failure);
            }
            _keyFrames.Writer.TryComplete();
        }
    }
    private List<uint> PendingRefresh()
    {
        var requested = _video.RefreshRequests();
        foreach (var source in _refresh.Keys.Except(requested.Keys).ToArray())
            if (_refresh[source].SentAt == 0 || Stopwatch.GetElapsedTime(_refresh[source].SentAt) >= _options.MinimumPictureLossInterval) _refresh.Remove(source);
        var pending = new List<uint>(8);
        if (!_session.VideoPictureLoss || !_session.CanReceiveVideo) return pending;
        foreach (var (source, revision) in requested)
        {
            if (!_refresh.TryGetValue(source, out var state)) { if (_refresh.Count == 8) continue; state = new(revision); _refresh.Add(source, state); }
            if (state.Revision != revision) { state.Revision = revision; state.Attempts = 0; }
            if (state.Attempts < _options.MaximumPictureLossAttempts && (state.SentAt == 0 || Stopwatch.GetElapsedTime(state.SentAt) >= _options.MinimumPictureLossInterval)) pending.Add(source);
        }
        return pending;
    }
    private TimeSpan RefreshDelay()
    {
        var delay = TimeSpan.FromDays(1);
        foreach (var state in _refresh.Values)
        {
            if (state.Attempts >= _options.MaximumPictureLossAttempts || state.SentAt == 0) continue;
            var remaining = _options.MinimumPictureLossInterval - Stopwatch.GetElapsedTime(state.SentAt);
            if (remaining > TimeSpan.Zero && remaining < delay) delay = remaining;
        }
        return delay;
    }
    private IEnumerable<Sender> LocalSenders()
    {
        if (_session.LocalAudio != null) yield return _audioSender;
        if (_session.LocalVideo != null) yield return _videoSender;
    }
    private (int Bytes, Reception[] Reports, List<uint> Feedback) Plan(List<uint> refresh)
    {
        var senders = LocalSenders().ToArray();
        var cnameBytes = (4 + 2 + Encoding.UTF8.GetByteCount(_cname) + 1 + 3) & ~3;
        var baseline = senders.Sum(s => s.SentAt == 0 ? 8 : 28) + 4 + cnameBytes * senders.Length;
        var feedback = refresh.Take(Math.Max(0, (_maximumBytes - baseline) / 12)).ToList();
        var sources = _received.Values.Where(r => r.Tracker.IsReady).ToArray();
        var count = Math.Min(sources.Length, Math.Max(0, (_maximumBytes - baseline - 12 * feedback.Count) / 24));
        var reports = new Reception[count];
        for (var i = 0; i < count; i++) reports[i] = sources[(_reportCursor + i) % sources.Length];
        return (baseline + count * 24 + feedback.Count * 12, reports, feedback);
    }
    private byte[] Build(Reception[] receptions, List<uint> refresh, out List<RtcpSenderReport> sentReports)
    {
        var ntp = RtcpClock.ToNtpTimestamp(DateTimeOffset.UtcNow);
        var names = new List<RtcpSdesChunk>(); var packets = new List<RtcpPacket>(); sentReports = [];
        var reports = new List<RtcpReceptionReport>();
        foreach (var r in receptions)
        {
            var sr = r.SenderReport;
            var report = r.Tracker.CreateReport(sr == null ? 0 : RtcpClock.Compact(sr.NtpTimestamp), sr == null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(r.ReceivedAt));
            if (report != null) reports.Add(report);
        }
        if (_received.Count != 0) _reportCursor = (_reportCursor + receptions.Length) % _received.Count;
        var included = (IReadOnlyList<RtcpReceptionReport>)reports.AsReadOnly();
        foreach (var sender in LocalSenders())
        {
            names.Add(new(sender.Source, _cname));
            if (sender.SentAt == 0) packets.Add(new RtcpReceiverReport(sender.Source, included));
            else
            {
                var timestamp = unchecked(sender.Timestamp + (uint)(Stopwatch.GetElapsedTime(sender.SentAt).TotalSeconds * sender.Clock));
                var sr = new RtcpSenderReport(sender.Source, ntp, timestamp, sender.Count, sender.Octets, included);
                packets.Add(sr); sentReports.Add(sr);
            }
            included = Array.Empty<RtcpReceptionReport>();
        }
        packets.Add(new RtcpSourceDescription(names.AsReadOnly()));
        foreach (var source in refresh) packets.Add(new RtcpPictureLossIndication(_videoSender.Source, source));
        return RtcpPackets.Encode(packets);
    }
    private TimeSpan BudgetDelay(int bytes)
    {
        var now = Stopwatch.GetTimestamp();
        _tokens = Math.Min(_maximumBytes + 128, _tokens + Stopwatch.GetElapsedTime(_budgetAt, now).TotalSeconds * _budget); _budgetAt = now;
        return _tokens >= bytes ? TimeSpan.Zero : TimeSpan.FromSeconds((bytes - _tokens) / _budget);
    }
    private void RememberReports(byte[] data, long sentAt)
    {
        RtcpPackets.TryParse(data, out var packets, out _);
        foreach (var sr in packets.OfType<RtcpSenderReport>()) _sentReports[(sr.SenderSource, RtcpClock.Compact(sr.NtpTimestamp))] = sentAt;
        PruneReports();
    }
    private void PruneReports()
    {
        foreach (var key in _sentReports.Where(p => Stopwatch.GetElapsedTime(p.Value) > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) _sentReports.Remove(key);
        while (_sentReports.Count > 32) _sentReports.Remove(_sentReports.MinBy(p => p.Value).Key);
    }
    internal PeerRtcpDiagnostics Diagnostics()
    { lock (_gate) return new(_received.Count, _sent, _read, _rejected, _pliSent, _pliReceived, _pliSuppressed, _rtt, _schedule.EarlyFeedbackEnabled); }
}
