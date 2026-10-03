using System.Buffers.Binary;

namespace tryAGI.WebRTC;

/// <summary>Bounded compound/reduced-size RTCP framing. Does not interpret feedback, SDES items or authenticate a sender.</summary>
public static class RtcpFraming
{
    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 8 or > 1200) return false;
        var count = 0;
        while (!data.IsEmpty)
        {
            if (++count > 32 || data.Length < 4 || data[0] >> 6 != 2 || data[1] is < 192 or > 223) return false;
            var length = 4 * (1 + BinaryPrimitives.ReadUInt16BigEndian(data[2..]));
            if (length > data.Length) return false;
            var content = length;
            if ((data[0] & 32) != 0)
            {
                if (length != data.Length || data[length - 1] == 0 || data[length - 1] > length - 4) return false;
                content -= data[length - 1];
            }
            var reports = data[0] & 31;
            if (count == 1 && content < 8) return false;
            if (data[1] == 200 && content != 28 + 24 * reports ||
                data[1] == 201 && content != 8 + 24 * reports ||
                data[1] == 203 && content < 4 + 4 * reports ||
                data[1] == 204 && content < 12 ||
                data[1] is 205 or 206 && content < 12 ||
                data[1] is 202 or 207 && content < 8) return false;
            data = data[length..];
        }
        return true;
    }
}
