using System.Buffers.Binary;

namespace tryAGI.WebRTC;

/// <summary>Bounded SCTP framing and CRC32C. Authentication belongs to DTLS, not this checksum.</summary>
public readonly ref struct SctpPacket
{
    private readonly ReadOnlySpan<byte> _bytes;
    private static readonly uint[] Table = MakeTable();
    private SctpPacket(ReadOnlySpan<byte> bytes) => _bytes = bytes;
    public ushort SourcePort => _bytes.Length >= 12 ? BinaryPrimitives.ReadUInt16BigEndian(_bytes) : (ushort)0;
    public ushort DestinationPort => _bytes.Length >= 12 ? BinaryPrimitives.ReadUInt16BigEndian(_bytes[2..]) : (ushort)0;
    public uint VerificationTag => _bytes.Length >= 12 ? BinaryPrimitives.ReadUInt32BigEndian(_bytes[4..]) : 0;
    public ChunkEnumerator Chunks => new(_bytes.Length >= 12 ? _bytes[12..] : []);

    public static bool TryParse(ReadOnlySpan<byte> bytes, out SctpPacket packet)
    {
        packet = default;
        if (bytes.Length is < 16 or > 65535 || BinaryPrimitives.ReadUInt16BigEndian(bytes) == 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]) == 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != ComputeChecksum(bytes)) return false;
        var rest = bytes[12..]; var count = 0;
        while (!rest.IsEmpty)
        {
            if (rest.Length < 4 || ++count > 256) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(rest[2..]);
            var padded = (length + 3) & ~3;
            if (length < 4 || padded > rest.Length) return false;
            rest = rest[padded..];
        }
        packet = new(bytes); return true;
    }

    /// <summary>CRC32C over the packet with the checksum field logically zeroed.</summary>
    public static uint ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        for (var i = 0; i < bytes.Length; i++)
            crc = Table[(crc ^ (i is >= 8 and < 12 ? 0u : bytes[i])) & 255] ^ (crc >> 8);
        return ~crc;
    }
    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0x82f63b78u);
            table[i] = value;
        }
        return table;
    }
    public ref struct ChunkEnumerator
    {
        private ReadOnlySpan<byte> _remaining;
        private SctpChunk _current;
        internal ChunkEnumerator(ReadOnlySpan<byte> bytes) { _remaining = bytes; _current = default; }
        public readonly ChunkEnumerator GetEnumerator() => this;
        public readonly SctpChunk Current => _current;
        public bool MoveNext()
        {
            if (_remaining.Length < 4) return false;
            var size = BinaryPrimitives.ReadUInt16BigEndian(_remaining[2..]); var padded = (size + 3) & ~3;
            if (size < 4 || padded > _remaining.Length) { _remaining = []; return false; }
            _current = new(_remaining[..size]); _remaining = _remaining[padded..]; return true;
        }
    }
}
public readonly ref struct SctpChunk
{
    private readonly ReadOnlySpan<byte> _bytes;
    internal SctpChunk(ReadOnlySpan<byte> bytes) => _bytes = bytes;
    public byte Type => _bytes.IsEmpty ? (byte)0 : _bytes[0];
    public byte Flags => _bytes.Length < 2 ? (byte)0 : _bytes[1];
    public ReadOnlySpan<byte> Body => _bytes.Length < 4 ? [] : _bytes[4..];
}

internal static class SctpWire
{
    internal static bool After(uint a, uint b) => unchecked((int)(a - b)) > 0;
    internal static bool After(ushort a, ushort b) => unchecked((short)(a - b)) > 0;
    internal static byte[] Chunk(byte type, byte flags, ReadOnlySpan<byte> body)
    {
        var result = new byte[(body.Length + 7) & ~3]; result[0] = type; result[1] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), checked((ushort)(body.Length + 4)));
        body.CopyTo(result.AsSpan(4)); return result;
    }
    internal static byte[] Packet(ushort source, ushort destination, uint tag, ReadOnlySpan<byte> chunk)
    {
        var result = new byte[12 + chunk.Length];
        BinaryPrimitives.WriteUInt16BigEndian(result, source); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), destination);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), tag); chunk.CopyTo(result.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), SctpPacket.ComputeChecksum(result)); return result;
    }
    internal static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16BigEndian(bytes);
    internal static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32BigEndian(bytes);
    internal static void U16(Span<byte> bytes, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    internal static void U32(Span<byte> bytes, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
}
