using System.Buffers.Binary;
using System.Text;

namespace tryAGI.WebRTC;

public abstract record RtcpPacket;
public sealed record RtcpReceptionReport(uint Source, byte FractionLost, int CumulativeLost, uint HighestSequence,
    uint Jitter, uint LastSenderReport, uint DelaySinceLastSenderReport);
public sealed record RtcpSenderReport(uint SenderSource, ulong NtpTimestamp, uint RtpTimestamp, uint PacketCount,
    uint OctetCount, IReadOnlyList<RtcpReceptionReport> Reports) : RtcpPacket;
public sealed record RtcpReceiverReport(uint SenderSource, IReadOnlyList<RtcpReceptionReport> Reports) : RtcpPacket;
/// <summary>CNAME view of an SDES chunk; other SDES items remain available in the original datagram.</summary>
public sealed record RtcpSdesChunk(uint Source, string? CanonicalName);
public sealed record RtcpSourceDescription(IReadOnlyList<RtcpSdesChunk> Chunks) : RtcpPacket;
public sealed record RtcpGoodbye(IReadOnlyList<uint> Sources, string? Reason) : RtcpPacket;
public sealed record RtcpPictureLossIndication(uint SenderSource, uint MediaSource) : RtcpPacket;
/// <summary>Unsupported packet semantics; never an actionable feedback event.</summary>
public sealed record RtcpOpaquePacket(byte Type, byte Count, ReadOnlyMemory<byte> Body) : RtcpPacket;

/// <summary>Bounded RTCP semantic views and SR/RR/CNAME/PLI encoding. Does not authenticate or authorize sources.</summary>
public static class RtcpPackets
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>Parses at most 1200 bytes and 32 packets. Returned views own their data.
    /// Compound means a leading SR/RR and CNAME for each interpreted report sender; it does not imply authentication,
    /// negotiated reduced-size support, authorized feedback or a known participant topology.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out IReadOnlyList<RtcpPacket> packets, out bool compound)
    {
        packets = []; compound = false;
        if (!RtcpFraming.IsValid(data)) return false;
        var parsed = new List<RtcpPacket>(8);
        try
        {
            while (!data.IsEmpty)
            {
                var length = 4 * (1 + U16(data[2..])); var content = length;
                if ((data[0] & 32) != 0) content -= data[length - 1];
                var body = data.Slice(4, content - 4); var count = data[0] & 31;
                switch (data[1])
                {
                    case 200:
                        parsed.Add(new RtcpSenderReport(U32(body), U64(body[4..]), U32(body[12..]), U32(body[16..]), U32(body[20..]), ReadReports(body[24..], count))); break;
                    case 201: parsed.Add(new RtcpReceiverReport(U32(body), ReadReports(body[4..], count))); break;
                    case 202:
                        var chunks = new List<RtcpSdesChunk>(count); var offset = 0;
                        for (var i = 0; i < count; i++)
                        {
                            if (body.Length - offset < 4) return false;
                            var start = offset; var source = U32(body[offset..]); offset += 4; string? name = null; var ended = false;
                            while (offset < body.Length)
                            {
                                var type = body[offset++];
                                if (type == 0) { ended = true; break; }
                                if (offset == body.Length) return false;
                                var size = body[offset++]; if (body.Length - offset < size) return false;
                                var value = body.Slice(offset, size);
                                if (type == 1)
                                { if (name != null || size == 0 || value.IndexOf((byte)0) >= 0) return false; name = Utf8.GetString(value); }
                                else if (type == 8 && (size == 0 || value[0] > size - 1)) return false;
                                offset += size;
                            }
                            if (!ended) return false;
                            while ((offset - start) % 4 != 0)
                            { if (offset == body.Length || body[offset++] != 0) return false; }
                            if (chunks.Any(c => c.Source == source)) return false;
                            chunks.Add(new(source, name));
                        }
                        if (offset != body.Length || count == 0) return false;
                        parsed.Add(new RtcpSourceDescription(chunks.AsReadOnly())); break;
                    case 203:
                        if (count == 0 || body.Length < 4 * count) return false;
                        var sources = new uint[count]; for (var i = 0; i < count; i++) sources[i] = U32(body[(4 * i)..]);
                        if (sources.Distinct().Count() != count) return false;
                        var tail = body[(4 * count)..]; string? reason = null;
                        if (!tail.IsEmpty)
                        {
                            var size = tail[0]; if (tail.Length < size + 1 || tail.Length - size - 1 > 3 || tail[(size + 1)..].IndexOfAnyExcept((byte)0) >= 0) return false;
                            reason = Utf8.GetString(tail.Slice(1, size));
                        }
                        parsed.Add(new RtcpGoodbye(Array.AsReadOnly(sources), reason)); break;
                    case 206 when count == 1:
                        if (body.Length != 8) return false;
                        parsed.Add(new RtcpPictureLossIndication(U32(body), U32(body[4..]))); break;
                    default: parsed.Add(new RtcpOpaquePacket(data[1], (byte)count, body.ToArray())); break;
                }
                data = data[length..];
            }
        }
        catch (DecoderFallbackException) { return false; }
        var names = new Dictionary<uint, string>();
        foreach (var chunk in parsed.OfType<RtcpSourceDescription>().SelectMany(s => s.Chunks))
            if (chunk.CanonicalName is { } name)
            { if (names.TryGetValue(chunk.Source, out var prior) && prior != name) return false; names[chunk.Source] = name; }
        compound = (parsed[0] is RtcpSenderReport or RtcpReceiverReport) &&
            parsed.Where(p => p is RtcpSenderReport or RtcpReceiverReport).All(p => names.ContainsKey(Sender(p)));
        packets = parsed.AsReadOnly(); return true;
    }
    private static IReadOnlyList<RtcpReceptionReport> ReadReports(ReadOnlySpan<byte> data, int count)
    {
        var reports = new RtcpReceptionReport[count];
        for (var i = 0; i < count; i++)
        {
            var block = data.Slice(i * 24, 24);
            var loss = (block[5] << 16) | (block[6] << 8) | block[7]; if ((loss & 0x800000) != 0) loss |= unchecked((int)0xff000000);
            reports[i] = new(U32(block), block[4], loss, U32(block[8..]), U32(block[12..]), U32(block[16..]), U32(block[20..]));
        }
        return Array.AsReadOnly(reports);
    }
    public static uint Sender(RtcpPacket packet) => packet switch
    {
        RtcpSenderReport sr => sr.SenderSource, RtcpReceiverReport rr => rr.SenderSource,
        RtcpPictureLossIndication pli => pli.SenderSource,
        _ => throw new ArgumentException("Packet has no interpreted sender source.", nameof(packet))
    };
    /// <summary>Encodes bounded SR/RR/CNAME/PLI packets. Caller supplies authenticated-session source identities,
    /// negotiated feedback/reduced-size policy and a valid compound ordering when required.</summary>
    public static byte[] Encode(IEnumerable<RtcpPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var input = packets.Take(33).ToArray();
        if (input.Length is < 1 or > 32 || input.Any(p => p == null)) throw new ArgumentOutOfRangeException(nameof(packets));
        using var output = new MemoryStream(1200);
        Span<byte> header = stackalloc byte[4];
        Span<byte> id = stackalloc byte[4];
        foreach (var packet in input)
        {
            byte type, count; byte[] body;
            switch (packet)
            {
                case RtcpSenderReport sr:
                    count = Count(sr.Reports); type = 200; body = new byte[24 + 24 * count];
                    W32(body, sr.SenderSource); BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4), sr.NtpTimestamp);
                    W32(body.AsSpan(12), sr.RtpTimestamp); W32(body.AsSpan(16), sr.PacketCount); W32(body.AsSpan(20), sr.OctetCount);
                    WriteReports(body.AsSpan(24), sr.Reports); break;
                case RtcpReceiverReport rr:
                    count = Count(rr.Reports); type = 201; body = new byte[4 + 24 * count]; W32(body, rr.SenderSource); WriteReports(body.AsSpan(4), rr.Reports); break;
                case RtcpSourceDescription sdes:
                    count = Count(sdes.Chunks); if (count == 0) throw new ArgumentException("Empty SDES.", nameof(packets)); type = 202;
                    using (var storage = new MemoryStream(1200))
                    {
                        var unique = new HashSet<uint>();
                        foreach (var chunk in sdes.Chunks)
                        {
                            if (chunk == null || !unique.Add(chunk.Source)) throw new ArgumentException("Invalid SDES source.", nameof(packets));
                            W32(id, chunk.Source); storage.Write(id);
                            if (chunk.CanonicalName is { } name)
                            {
                                var size = Utf8.GetByteCount(name);
                                if (size is < 1 or > 255 || name.Contains('\0')) throw new ArgumentException("Invalid CNAME.", nameof(packets));
                                storage.WriteByte(1); storage.WriteByte((byte)size); storage.Write(Utf8.GetBytes(name));
                            }
                            storage.WriteByte(0); while (storage.Length % 4 != 0) storage.WriteByte(0);
                            if (storage.Length > 1196) throw new ArgumentOutOfRangeException(nameof(packets));
                        }
                        body = storage.ToArray();
                    }
                    break;
                case RtcpPictureLossIndication pli:
                    count = 1; type = 206; body = new byte[8]; W32(body, pli.SenderSource); W32(body.AsSpan(4), pli.MediaSource); break;
                default: throw new NotSupportedException("Encoding supports SR, RR, CNAME and PLI; other packet semantics remain caller-owned.");
            }
            if (output.Length + 4 + body.Length > 1200) throw new ArgumentOutOfRangeException(nameof(packets));
            header[0] = (byte)(0x80 | count); header[1] = type;
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(body.Length / 4)); output.Write(header); output.Write(body);
        }
        return output.ToArray();
    }
    private static byte Count<T>(IReadOnlyList<T> items)
    { if (items == null || items.Count > 31) throw new ArgumentOutOfRangeException(nameof(items)); return (byte)items.Count; }
    private static void WriteReports(Span<byte> data, IReadOnlyList<RtcpReceptionReport> reports)
    {
        for (var i = 0; i < reports.Count; i++)
        {
            var r = reports[i]; if (r == null || r.CumulativeLost is < -8388608 or > 8388607) throw new ArgumentOutOfRangeException(nameof(reports));
            var block = data.Slice(i * 24, 24); W32(block, r.Source); block[4] = r.FractionLost;
            block[5] = (byte)(r.CumulativeLost >> 16); block[6] = (byte)(r.CumulativeLost >> 8); block[7] = (byte)r.CumulativeLost;
            W32(block[8..], r.HighestSequence); W32(block[12..], r.Jitter); W32(block[16..], r.LastSenderReport); W32(block[20..], r.DelaySinceLastSenderReport);
        }
    }
    private static ushort U16(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16BigEndian(value);
    private static uint U32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);
    private static ulong U64(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt64BigEndian(value);
    private static void W32(Span<byte> value, uint number) => BinaryPrimitives.WriteUInt32BigEndian(value, number);
}
