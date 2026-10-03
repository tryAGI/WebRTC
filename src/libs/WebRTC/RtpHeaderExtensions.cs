namespace tryAGI.WebRTC;

/// <summary>RFC 8285 element framing. Unknown profiles have no readable elements; no packet authentication is performed here.</summary>
public static class RtpHeaderExtensions
{
    public static bool TryRead(in RtpPacket packet, int id, out ReadOnlySpan<byte> value)
    {
        if (id is < 1 or > 255) { value = default; return false; }
        return TryValidateAndRead(packet, id, out value, out var present) && present;
    }
    /// <summary>Distinguishes absent/unknown extensions from malformed known framing. ID zero validates framing only.</summary>
    public static bool TryValidateAndRead(in RtpPacket packet, int id, out ReadOnlySpan<byte> value, out bool present)
    {
        value = default;
        present = false;
        if (id is < 0 or > 255) return false;
        var data = packet.ExtensionData;
        var one = packet.ExtensionProfile == 0xBEDE;
        if (!one && (packet.ExtensionProfile & 0xFFF0) != 0x1000) return true;
        var found = false;
        for (var offset = 0; offset < data.Length;)
        {
            var header = data[offset++];
            if (header == 0) continue;
            int element, length;
            if (one)
            {
                element = header >> 4;
                if (element == 15) break;
                if (element == 0) { value = default; return false; }
                length = (header & 15) + 1;
            }
            else
            {
                element = header;
                if (offset == data.Length) { value = default; return false; }
                length = data[offset++];
            }
            if (length > data.Length - offset || element == id && found) { value = default; return false; }
            if (element == id) { value = data.Slice(offset, length); found = true; }
            offset += length;
        }
        present = found; return true;
    }
}
