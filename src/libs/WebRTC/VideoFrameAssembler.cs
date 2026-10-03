using System.Buffers.Binary;
using System.Text;

namespace tryAGI.WebRTC;

public enum VideoCodec { H264, Vp8 }

/// <summary>One complete encoded frame. H264 uses four-byte Annex B NAL prefixes; VP8 is the descriptor-free bitstream. No codec decoding is performed.</summary>
public sealed record EncodedVideoFrame(VideoCodec Codec, uint SynchronizationSource, uint Timestamp,
    ushort FirstSequenceNumber, ushort LastSequenceNumber, bool IsKeyFrame, byte[] Payload);

public sealed record VideoFrameAssemblerOptions
{
    public required VideoCodec Codec { get; init; }
    public required byte PayloadType { get; init; }
    /// <summary>An explicitly negotiated/authorized remote SSRC. The assembler never learns sources from unauthenticated input.</summary>
    public required uint SynchronizationSource { get; init; }
    public int H264PacketizationMode { get; init; } = 1;
    public string? Mid { get; init; }
    public int MidExtensionId { get; init; }
    public int MaximumFrames { get; init; } = 4;
    public int MaximumPacketsPerFrame { get; init; } = 1024;
    public int MaximumFrameBytes { get; init; } = 2 * 1024 * 1024;
    public int MaximumBufferedBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumPacketBytes { get; init; } = 4096;
    public TimeSpan MaximumFrameAge { get; init; } = TimeSpan.FromMilliseconds(250);
}

public sealed record VideoFrameAssemblerDiagnostics(int BufferedFrames, int BufferedBytes, int BufferedPackets,
    long CompletedFrames, long DroppedFrames, long RejectedPackets, long DuplicatePackets);

/// <summary>Bounded single-source H264 mode 0/1 or VP8 reassembly. Input MUST already have passed SRTP authentication/replay checks.
/// H264 waits for a prior access-unit marker before emitting frames; FU start alone cannot prove an access-unit boundary.
/// Call Expire periodically even when media is silent. Dispose releases incomplete frames; this is not a decoder/jitter buffer.</summary>
public sealed class VideoFrameAssembler : IDisposable
{
    private readonly object _gate = new();
    private readonly VideoFrameAssemblerOptions _options;
    private readonly TimeProvider _time;
    private readonly byte[] _mid;
    private readonly Dictionary<uint, Frame> _frames = [];
    private readonly SortedSet<long> _boundaries = [];
    private readonly Queue<uint> _retired = [];
    private long? _highest;
    private long _deliveredThrough = long.MinValue;
    private int _bytes;
    private long _completed, _dropped, _rejected, _duplicates;
    private bool _disposed;
    private sealed class Frame(uint timestamp, long born)
    {
        internal readonly uint Timestamp = timestamp;
        internal readonly long Born = born;
        internal readonly SortedDictionary<long, Part> Parts = [];
        internal long? Start, End;
        internal int Bytes, Storage;
        internal int? PictureId;
    }
    private sealed record Part(byte[] Payload, VideoPayload Info, bool Marker);

    public VideoFrameAssembler(VideoFrameAssemblerOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Codec) || options.PayloadType is < 96 or > 127 || options.SynchronizationSource == 0 ||
            options.H264PacketizationMode is < 0 or > 1 || options.MaximumFrames is < 1 or > 8 ||
            options.MaximumPacketsPerFrame is < 1 or > 4096 || options.MaximumFrameBytes is < 64 or > 8 * 1024 * 1024 ||
            options.MaximumBufferedBytes < options.MaximumFrameBytes || options.MaximumBufferedBytes > 16 * 1024 * 1024 ||
            options.MaximumPacketBytes is < 12 or > 4096 || options.MidExtensionId is < 0 or > 255 ||
            options.Mid == null && options.MidExtensionId != 0 || options.Mid != null &&
            (options.MidExtensionId == 0 || !SdpSessionDescription.Token(options.Mid, 64)) ||
            options.MaximumFrameAge < TimeSpan.FromMilliseconds(10) || options.MaximumFrameAge > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(options));
        _options = options; _time = timeProvider ?? TimeProvider.System;
        _mid = options.Mid == null ? [] : Encoding.ASCII.GetBytes(options.Mid);
    }

    /// <summary>Copies admitted payloads. Returns newly complete frames in RTP sequence order, without an additional playout delay.</summary>
    public IReadOnlyList<EncodedVideoFrame> Push(ReadOnlySpan<byte> authenticatedRtp)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ExpireCore();
            if (authenticatedRtp.Length > _options.MaximumPacketBytes || !RtpPacket.TryParse(authenticatedRtp, out var packet) ||
                packet.PayloadType != _options.PayloadType || packet.SynchronizationSource != _options.SynchronizationSource ||
                !RtpHeaderExtensions.TryValidateAndRead(packet, _options.MidExtensionId, out var mid, out var present) ||
                present && !mid.SequenceEqual(_mid)) { _rejected++; return []; }
            var sequence = _highest == null ? packet.SequenceNumber : _highest.Value + unchecked((short)(packet.SequenceNumber - (ushort)_highest.Value));
            if (_highest != null && sequence - _highest.Value >= 8192)
            {
                foreach (var stale in _frames.Values.ToArray()) Drop(stale.Timestamp);
                _boundaries.Clear();
            }
            if (_highest != null && sequence - _highest.Value <= -8192 || sequence <= _deliveredThrough || _retired.Contains(packet.Timestamp))
            { _rejected++; return []; }
            _highest = Math.Max(_highest ?? sequence, sequence);
            if (packet.Marker)
            {
                _boundaries.Add(sequence);
                if (_boundaries.Count > 32) _boundaries.Remove(_boundaries.Min);
            }
            if (!VideoPayload.TryRead(packet.Payload, _options.Codec, _options.H264PacketizationMode, out var info))
            { _rejected++; Drop(packet.Timestamp); Retire(packet.Timestamp); return []; }
            foreach (var other in _frames.Values)
                if (other.Timestamp != packet.Timestamp && other.Parts.ContainsKey(sequence))
                { _rejected++; Drop(other.Timestamp); Drop(packet.Timestamp); Retire(packet.Timestamp); return []; }
            if (!_frames.TryGetValue(packet.Timestamp, out var frame))
            {
                if (_frames.Count == _options.MaximumFrames) Drop(_frames.Values.MinBy(f => f.Born)!.Timestamp);
                frame = new(packet.Timestamp, _time.GetTimestamp()); _frames.Add(packet.Timestamp, frame);
            }
            if (frame.Parts.TryGetValue(sequence, out var existing))
            {
                if (existing.Marker == packet.Marker && existing.Payload.AsSpan().SequenceEqual(packet.Payload)) _duplicates++;
                else { _rejected++; Drop(frame.Timestamp); }
                return [];
            }
            if (frame.Parts.Count == _options.MaximumPacketsPerFrame || info.Length > _options.MaximumFrameBytes - frame.Bytes ||
                Math.Max(info.Length, packet.Payload.Length) > _options.MaximumBufferedBytes - _bytes || frame.End != null && packet.Marker && frame.End != sequence ||
                info.Start && frame.Start != null && frame.Start != sequence ||
                info.PictureId != null && frame.PictureId != null && info.PictureId != frame.PictureId)
            { _rejected++; Drop(frame.Timestamp); return []; }
            frame.PictureId ??= info.PictureId;
            if (info.Start) frame.Start = sequence;
            if (packet.Marker) frame.End = sequence;
            frame.Parts.Add(sequence, new(packet.Payload.ToArray(), info, packet.Marker));
            frame.Bytes += info.Length; var storage = Math.Max(info.Length, packet.Payload.Length); frame.Storage += storage; _bytes += storage;
            var ready = new List<EncodedVideoFrame>();
            foreach (var pending in _frames.Values.OrderBy(f => f.Parts.First().Key).ToArray())
            {
                if (pending.End == null) continue;
                var start = pending.Start;
                if (_options.Codec == VideoCodec.H264)
                {
                    var earlier = _boundaries.GetViewBetween(long.MinValue, pending.Parts.First().Key - 1);
                    if (earlier.Count != 0) start = earlier.Max + 1;
                }
                if (start == null || pending.End < start || pending.End - start >= _options.MaximumPacketsPerFrame) continue;
                if (pending.Parts.First().Key < start || pending.Parts.Last().Key > pending.End)
                { _rejected++; Drop(pending.Timestamp); continue; }
                if (pending.Parts.Count != pending.End - start + 1) continue;
                var output = Assemble(pending);
                if (output == null) { _rejected++; Drop(pending.Timestamp); continue; }
                ready.Add(new(_options.Codec, _options.SynchronizationSource, pending.Timestamp, (ushort)start.Value,
                    (ushort)pending.End.Value, output.Value.Key, output.Value.Bytes));
                _completed++; _deliveredThrough = pending.End.Value;
                Remove(pending); Retire(pending.Timestamp);
                foreach (var obsolete in _frames.Values.Where(f => f.Parts.First().Key <= _deliveredThrough).ToArray()) Drop(obsolete.Timestamp);
            }
            return ready;
        }
    }

    private (byte[] Bytes, bool Key)? Assemble(Frame frame)
    {
        var output = new byte[frame.Bytes]; var offset = 0; byte? fragmented = null; var key = false; var partition = -1;
        foreach (var part in frame.Parts.Values)
        {
            var p = part.Payload.AsSpan(); var info = part.Info;
            if (_options.Codec == VideoCodec.Vp8)
            {
                if (info.Partition < partition || info.Partition > partition && !info.PartitionStart ||
                    info.Partition == partition && info.PartitionStart) return null;
                partition = info.Partition;
                p[info.Offset..].CopyTo(output.AsSpan(offset)); offset += info.Length;
                continue;
            }
            if (info.Kind != 28)
            {
                if (fragmented != null) return null;
                if (info.Kind == 24)
                {
                    var i = 1;
                    while (i < p.Length)
                    {
                        var length = BinaryPrimitives.ReadUInt16BigEndian(p[i..]); i += 2;
                        WriteNal(p.Slice(i, length), output, ref offset); key |= (p[i] & 31) == 5; i += length;
                    }
                }
                else { WriteNal(p, output, ref offset); key |= info.Kind == 5; }
            }
            else
            {
                var header = (byte)((p[0] & 0xE0) | (p[1] & 31));
                if ((p[1] & 128) != 0)
                {
                    if (fragmented != null) return null;
                    fragmented = header; output[offset + 3] = 1; output[offset + 4] = header; offset += 5; key |= (header & 31) == 5;
                }
                else if (fragmented != header) return null;
                p[2..].CopyTo(output.AsSpan(offset)); offset += p.Length - 2;
                if ((p[1] & 64) != 0) fragmented = null;
            }
        }
        if (fragmented != null || offset != output.Length || output.Length == 0) return null;
        if (_options.Codec == VideoCodec.Vp8)
        {
            if (output.Length < 3 || (output[0] & 1) == 0 && (output.Length < 10 || !output.AsSpan(3, 3).SequenceEqual(new byte[] { 0x9D, 1, 0x2A }))) return null;
            key = (output[0] & 1) == 0;
        }
        return (output, key);
    }
    private static void WriteNal(ReadOnlySpan<byte> nal, byte[] output, ref int offset)
    { output[offset + 3] = 1; offset += 4; nal.CopyTo(output.AsSpan(offset)); offset += nal.Length; }
    private void Retire(uint timestamp)
    { if (!_retired.Contains(timestamp)) { _retired.Enqueue(timestamp); if (_retired.Count > 32) _retired.Dequeue(); } }
    private void Remove(Frame frame) { _bytes -= frame.Storage; _frames.Remove(frame.Timestamp); }
    private void Drop(uint timestamp)
    { if (_frames.TryGetValue(timestamp, out var frame)) { Remove(frame); _dropped++; } Retire(timestamp); }
    private void ExpireCore()
    {
        foreach (var frame in _frames.Values.Where(f => _time.GetElapsedTime(f.Born) >= _options.MaximumFrameAge).ToArray()) Drop(frame.Timestamp);
    }
    public void Expire() { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); ExpireCore(); } }
    public VideoFrameAssemblerDiagnostics GetDiagnostics()
    { lock (_gate) return new(_frames.Count, _bytes, _frames.Values.Sum(f => f.Parts.Count), _completed, _dropped, _rejected, _duplicates); }
    /// <summary>Explicit new stream generation after application-validated restart/SSRC reuse. Does not authenticate signaling.</summary>
    public void Reset()
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _dropped += _frames.Count; _frames.Clear(); _bytes = 0; _boundaries.Clear(); _retired.Clear(); _highest = null; _deliveredThrough = long.MinValue; } }
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; _dropped += _frames.Count; _frames.Clear(); _bytes = 0; _boundaries.Clear(); _retired.Clear(); } }
}

internal readonly record struct VideoPayload(int Kind, int Offset, int Length, bool Start, int Partition, bool PartitionStart, int? PictureId)
{
    internal static bool TryRead(ReadOnlySpan<byte> p, VideoCodec codec, int mode, out VideoPayload result)
    {
        result = default;
        if (p.IsEmpty) return false;
        if (codec == VideoCodec.H264)
        {
            if ((p[0] & 128) != 0) return false; // Corrupted NALs are never emitted as complete frames.
            var type = p[0] & 31;
            if (type is >= 1 and <= 23) { result = new(type, 0, p.Length + 4, false, 0, false, null); return true; }
            if (mode == 0) return false;
            if (type == 28)
            {
                if (p.Length < 2 || (p[1] & 31) is < 1 or > 23 || (p[1] & 192) == 192) return false;
                result = new(type, 2, p.Length - 2 + ((p[1] & 128) != 0 ? 5 : 0), false, 0, false, null); return true;
            }
            if (type != 24 || p.Length < 4) return false;
            var i = 1; var length = 0;
            while (i < p.Length)
            {
                if (p.Length - i < 2) return false;
                var n = BinaryPrimitives.ReadUInt16BigEndian(p[i..]); i += 2;
                if (n == 0 || n > p.Length - i || (p[i] & 128) != 0 || (p[i] & 31) is < 1 or > 23) return false;
                length += n + 4; i += n;
            }
            result = new(type, 1, length, false, 0, false, null); return true;
        }
        var partitionStart = (p[0] & 16) != 0; var partition = p[0] & 7;
        var offset = 1; int? pictureId = null;
        if ((p[0] & 128) != 0)
        {
            if (p.Length <= offset) return false;
            var extension = p[offset++];
            if ((extension & 64) != 0 && (extension & 32) == 0) return false;
            if ((extension & 128) != 0)
            {
                if (p.Length <= offset) return false;
                var id = p[offset++]; pictureId = id & 127;
                if ((id & 128) != 0) { if (p.Length <= offset) return false; pictureId = pictureId * 256 + p[offset++]; }
            }
            if ((extension & 64) != 0) offset++;
            if ((extension & 48) != 0) offset++;
        }
        if (offset >= p.Length) return false;
        result = new(0, offset, p.Length - offset, partitionStart && partition == 0, partition, partitionStart, pictureId); return true;
    }
}
