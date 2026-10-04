using System.Buffers.Binary;
using System.Text;

namespace tryAGI.WebRTC;

public sealed record EncodedOpusPacket(uint SynchronizationSource, ushort SequenceNumber, uint Timestamp, bool Marker, byte[] Payload);

internal sealed class PeerAudio
{
    private readonly SdpNegotiatedSession _session;
    private readonly byte[] _remoteMid, _localMid;
    private readonly int _incomingMid, _outgoingMid, _sourceLimit;
    private readonly HashSet<uint> _excludedSources, _receivedSources = [];
    private readonly uint _source;
    internal int HeaderLength { get; }
    internal PeerAudio(SdpNegotiatedSession session, SdpSessionDescription remote, uint source, int sourceLimit)
    {
        _session = session; _source = source; _sourceLimit = sourceLimit;
        _remoteMid = Encoding.ASCII.GetBytes(session.RemoteAudio?.Mid ?? "");
        _localMid = Encoding.ASCII.GetBytes(session.LocalAudio?.Mid ?? "");
        _incomingMid = session.IncomingAudioHeaderExtensions.FirstOrDefault(e => e.Value == SdpNegotiation.MidExtension).Key;
        _outgoingMid = session.OutgoingAudioHeaderExtensions.FirstOrDefault(e => e.Value == SdpNegotiation.MidExtension).Key;
        _excludedSources = remote.Media.Where(m => m.Mid != session.RemoteAudio?.Mid).SelectMany(m => m.Sources).ToHashSet();
        if (session.RemoteAudio?.Sources.Any(s => _excludedSources.Contains(s)) == true)
            throw new ArgumentException("Ambiguous remote audio source.", nameof(remote));
        HeaderLength = 12 + (_outgoingMid == 0 ? 0 : 4 + ExtensionLength());
    }
    private bool OneByte => _outgoingMid < 15 && _localMid.Length <= 16;
    private int ExtensionLength() => (_localMid.Length + (OneByte ? 1 : 2) + 3) & ~3;
    internal byte[] Write(ReadOnlySpan<byte> payload, ushort sequence, uint timestamp, bool marker)
    {
        var data = new byte[HeaderLength + payload.Length];
        data[0] = _outgoingMid == 0 ? (byte)0x80 : (byte)0x90;
        data[1] = (byte)(_session.AudioCodec!.PayloadType | (marker ? 128 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), timestamp);
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
    internal bool HasSource(uint source) => _receivedSources.Contains(source);
    internal EncodedOpusPacket? Read(byte[] data)
    {
        if (!_session.CanReceiveAudio || !RtpPacket.TryParse(data, out var packet) ||
            packet.PayloadType != _session.AudioCodec!.PayloadType || packet.Payload.Length is < 1 or > 1275 ||
            packet.SynchronizationSource == 0 || packet.SynchronizationSource == _source || _excludedSources.Contains(packet.SynchronizationSource)) return null;
        if (!RtpHeaderExtensions.TryValidateAndRead(packet, _incomingMid, out var mid, out var present) ||
            present && !mid.SequenceEqual(_remoteMid)) return null;
        if (!_receivedSources.Contains(packet.SynchronizationSource))
        {
            if (_receivedSources.Count == _sourceLimit) return null;
            _receivedSources.Add(packet.SynchronizationSource);
        }
        return new(packet.SynchronizationSource, packet.SequenceNumber, packet.Timestamp, packet.Marker, packet.Payload.ToArray());
    }
}
