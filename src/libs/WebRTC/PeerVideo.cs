using System.Buffers.Binary;
using System.Text;

namespace tryAGI.WebRTC;

public sealed record PeerVideoOptions
{
    public int MaximumSources { get; init; } = 4;
    public int QueueCapacity { get; init; } = 2;
    public int MaximumBufferedBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumFrameBytes { get; init; } = 2 * 1024 * 1024;
    public TimeSpan MaximumFrameAge { get; init; } = TimeSpan.FromMilliseconds(250);
}
public sealed record PeerVideoDiagnostics(int Sources, int BufferedBytes, int BufferedFrames, long RejectedPackets, long CompletedFrames, long DroppedFrames, long DroppedQueueFrames);

internal sealed class PeerVideo : IDisposable
{
    private readonly object _gate = new();
    private readonly SdpNegotiatedSession _session;
    private readonly PeerVideoOptions _options;
    private readonly Dictionary<uint, VideoFrameAssembler> _sources = [];
    private readonly HashSet<uint> _excluded, _announced;
    private readonly uint _source;
    private readonly int _incomingMid, _outgoingMid;
    private readonly byte[] _remoteMid, _localMid;
    private readonly Dictionary<uint, long> _refresh = [];
    private long _revision;
    private long _rejected;
    private bool _disposed;
    internal int HeaderLength { get; }
    internal PeerVideo(SdpNegotiatedSession session, SdpSessionDescription remote, uint source, uint audioSource, PeerVideoOptions options)
    {
        _session = session; _source = source; _options = options;
        _excluded = remote.Media.Where(m => m.Mid != session.RemoteVideo?.Mid).SelectMany(m => m.Sources).ToHashSet();
        _excluded.Add(source); _excluded.Add(audioSource);
        _announced = session.RemoteVideo?.Sources.ToHashSet() ?? [];
        if (_announced.Any(_excluded.Contains)) throw new ArgumentException("Ambiguous remote video source.", nameof(remote));
        _incomingMid = session.IncomingVideoHeaderExtensions.FirstOrDefault(e => e.Value == SdpNegotiation.MidExtension).Key;
        _outgoingMid = session.OutgoingVideoHeaderExtensions.FirstOrDefault(e => e.Value == SdpNegotiation.MidExtension).Key;
        _remoteMid = Encoding.ASCII.GetBytes(session.RemoteVideo?.Mid ?? ""); _localMid = Encoding.ASCII.GetBytes(session.LocalVideo?.Mid ?? "");
        HeaderLength = 12 + (_outgoingMid == 0 ? 0 : 4 + ExtensionLength());
    }
    internal static void Validate(PeerVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumSources is < 1 or > 8 || options.QueueCapacity is < 1 or > 8 ||
            options.MaximumFrameBytes is < 64 or > 8 * 1024 * 1024 || options.MaximumBufferedBytes < options.MaximumFrameBytes ||
            options.MaximumBufferedBytes > 16 * 1024 * 1024 || (long)options.QueueCapacity * options.MaximumFrameBytes > 16 * 1024 * 1024 ||
            options.MaximumFrameAge < TimeSpan.FromMilliseconds(10) || options.MaximumFrameAge > TimeSpan.FromSeconds(2)) throw new ArgumentOutOfRangeException(nameof(options));
    }
    private bool OneByte => _outgoingMid < 15 && _localMid.Length <= 16;
    private int ExtensionLength() => (_localMid.Length + (OneByte ? 1 : 2) + 3) & ~3;
    internal byte[] Write(ReadOnlySpan<byte> payload, ushort sequence, uint timestamp, bool marker)
    {
        if (!VideoPayload.TryRead(payload, _session.VideoFormat!.Codec, _session.VideoFormat.H264PacketizationMode, out _))
            throw new ArgumentException("Payload is not a valid negotiated video RTP fragment.", nameof(payload));
        var data = new byte[HeaderLength + payload.Length]; data[0] = _outgoingMid == 0 ? (byte)0x80 : (byte)0x90;
        data[1] = (byte)(_session.VideoFormat.PayloadType | (marker ? 128 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), sequence); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), _source);
        if (_outgoingMid != 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), OneByte ? (ushort)0xBEDE : (ushort)0x1000);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), (ushort)(ExtensionLength() / 4));
            if (OneByte) data[16] = (byte)(_outgoingMid << 4 | _localMid.Length - 1);
            else { data[16] = (byte)_outgoingMid; data[17] = (byte)_localMid.Length; }
            _localMid.CopyTo(data, OneByte ? 17 : 18);
        }
        payload.CopyTo(data.AsSpan(HeaderLength)); return data;
    }
    internal void RejectSource() { lock (_gate) _rejected++; }
    internal bool HasSource(uint source) { lock (_gate) return _sources.ContainsKey(source); }
    internal IReadOnlyDictionary<uint, long> RefreshRequests() { lock (_gate) return new Dictionary<uint, long>(_refresh); }
    internal void RequestRefresh(uint source)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sources.ContainsKey(source) && !_announced.Contains(source)) throw new ArgumentException("Video source is not authorized.", nameof(source));
            if (!_refresh.ContainsKey(source) && _refresh.Count == 8) throw new InvalidOperationException("Video refresh source budget is full.");
            _refresh[source] = ++_revision;
        }
    }
    internal bool QueueLoss(uint source) { lock (_gate) return MarkRefresh(source); }
    private bool MarkRefresh(uint source)
    {
        if (_disposed || _refresh.ContainsKey(source) || _refresh.Count == 8) return false;
        _refresh[source] = ++_revision; return true;
    }
    internal bool Read(ReadOnlySpan<byte> data, out IReadOnlyList<EncodedVideoFrame> frames, out bool refreshChanged)
    {
        lock (_gate)
        {
            var before = _revision; var valid = ReadCore(data, out frames); refreshChanged = before != _revision; return valid;
        }
    }
    private bool ReadCore(ReadOnlySpan<byte> data, out IReadOnlyList<EncodedVideoFrame> frames)
    {
        lock (_gate)
        {
            frames = []; ExpireCore();
            if (!_session.CanReceiveVideo || data.Length > 4096 || !RtpPacket.TryParse(data, out var packet) ||
                packet.PayloadType != _session.VideoFormat!.PayloadType || packet.SynchronizationSource == 0 ||
                _excluded.Contains(packet.SynchronizationSource) || _announced.Count != 0 && !_announced.Contains(packet.SynchronizationSource) ||
                !RtpHeaderExtensions.TryValidateAndRead(packet, _incomingMid, out var mid, out var present) || present && !mid.SequenceEqual(_remoteMid) ||
                !VideoPayload.TryRead(packet.Payload, _session.VideoFormat.Codec, _session.VideoFormat.H264PacketizationMode, out var payload)) { _rejected++; return false; }
            if (_sources.Values.Sum(s => s.GetDiagnostics().BufferedBytes) + Math.Max(packet.Payload.Length, payload.Length) > _options.MaximumBufferedBytes)
            { _rejected++; return false; }
            if (!_sources.TryGetValue(packet.SynchronizationSource, out var assembler))
            {
                if (_sources.Count == _options.MaximumSources) { _rejected++; return false; }
                assembler = new(new() { Codec = _session.VideoFormat.Codec, PayloadType = packet.PayloadType,
                    SynchronizationSource = packet.SynchronizationSource, H264PacketizationMode = _session.VideoFormat.H264PacketizationMode,
                    MidExtensionId = _incomingMid, Mid = _incomingMid == 0 ? null : _session.RemoteVideo!.Mid,
                    MaximumFrameBytes = _options.MaximumFrameBytes, MaximumBufferedBytes = _options.MaximumBufferedBytes, MaximumFrameAge = _options.MaximumFrameAge });
                _sources.Add(packet.SynchronizationSource, assembler); MarkRefresh(packet.SynchronizationSource);
            }
            var prior = assembler.GetDiagnostics();
            frames = assembler.Push(data);
            if (assembler.GetDiagnostics().DroppedFrames > prior.DroppedFrames) MarkRefresh(packet.SynchronizationSource);
            foreach (var frame in frames) if (frame.IsKeyFrame && _refresh.Remove(frame.SynchronizationSource)) _revision++;
            return true;
        }
    }
    private void ExpireCore()
    {
        if (_disposed) return;
        foreach (var (source, assembler) in _sources)
        {
            var prior = assembler.GetDiagnostics().DroppedFrames; assembler.Expire();
            if (assembler.GetDiagnostics().DroppedFrames > prior) MarkRefresh(source);
        }
    }
    internal bool Expire() { lock (_gate) { var before = _revision; ExpireCore(); return before != _revision; } }
    internal PeerVideoDiagnostics Diagnostics(long droppedQueue)
    {
        lock (_gate)
        {
            var d = _sources.Values.Select(s => s.GetDiagnostics()).ToArray();
            return new(_sources.Count, d.Sum(s => s.BufferedBytes), d.Sum(s => s.BufferedFrames), _rejected + d.Sum(s => s.RejectedPackets),
                d.Sum(s => s.CompletedFrames), d.Sum(s => s.DroppedFrames), droppedQueue);
        }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _refresh.Clear(); foreach (var s in _sources.Values) s.Dispose(); } }
}
