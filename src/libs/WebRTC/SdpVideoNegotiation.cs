using System.Globalization;
using System.Text;

namespace tryAGI.WebRTC;

/// <summary>Capabilities supplied by the application's external codec, not a claim that this library decodes video.</summary>
public sealed record VideoCodecCapability
{
    public required VideoCodec Codec { get; init; }
    public required byte PayloadType { get; init; }
    public string? H264ProfileLevelId { get; init; }
    public int H264PacketizationMode { get; init; } = 1;
    public bool H264LevelAsymmetryAllowed { get; init; }
    public int Vp8MaximumMacroblocks { get; init; }
    public int Vp8MaximumFrameRate { get; init; }
    internal SdpRtpCodec ToCodec()
    {
        if (!Enum.IsDefined(Codec) || PayloadType is < 96 or > 127 || PayloadType == 111 || H264PacketizationMode is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(PayloadType));
        string fmtp;
        if (Codec == VideoCodec.H264)
        {
            if (!H264Profile.TryRead(H264ProfileLevelId, out _)) throw new ArgumentException("An explicit valid external H264 profile/level is required.");
            fmtp = $"profile-level-id={H264ProfileLevelId!.ToLowerInvariant()};packetization-mode={H264PacketizationMode};level-asymmetry-allowed={(H264LevelAsymmetryAllowed ? 1 : 0)}";
        }
        else
        {
            if (Vp8MaximumMacroblocks is < 1 or > 65536 || Vp8MaximumFrameRate is < 1 or > 1000)
                throw new ArgumentOutOfRangeException(nameof(Vp8MaximumMacroblocks));
            fmtp = $"max-fs={Vp8MaximumMacroblocks};max-fr={Vp8MaximumFrameRate}";
        }
        return new(PayloadType, Codec == VideoCodec.H264 ? "H264" : "VP8", 90000, 1, fmtp);
    }
}

/// <summary>Selected transport format and directional codec limits. Parameter sets belong to their declaring sender; they are not prepended to frames.</summary>
public sealed class SdpVideoFormat
{
    public VideoCodec Codec { get; }
    public byte PayloadType { get; }
    public int H264PacketizationMode { get; }
    public string? LocalReceiveProfileLevelId { get; }
    public string? RemoteReceiveProfileLevelId { get; }
    public int? RemoteVp8MaximumMacroblocks { get; }
    public int? RemoteVp8MaximumFrameRate { get; }
    public IReadOnlyList<string> RemoteH264ParameterSets { get; }
    internal SdpVideoFormat(SdpRtpCodec local, SdpRtpCodec remote)
    {
        if (!VideoParameters.TryRead(local, out var l) || !VideoParameters.TryRead(remote, out var r) || !l.Compatible(r))
            throw new InvalidOperationException("Incompatible video format.");
        Codec = l.Codec; PayloadType = local.PayloadType; H264PacketizationMode = l.Mode;
        var asymmetry = l.Asymmetry && r.Asymmetry;
        LocalReceiveProfileLevelId = l.Profile?.AtLevel(asymmetry ? l.Profile.Value.Rank : Math.Min(l.Profile!.Value.Rank, r.Profile!.Value.Rank));
        RemoteReceiveProfileLevelId = r.Profile?.AtLevel(asymmetry ? r.Profile.Value.Rank : Math.Min(l.Profile!.Value.Rank, r.Profile!.Value.Rank));
        RemoteVp8MaximumMacroblocks = r.MaxFs; RemoteVp8MaximumFrameRate = r.MaxFr;
        // A level downgrade invalidates the declaring sender's out-of-band sets.
        RemoteH264ParameterSets = Array.AsReadOnly(r.Profile != null && r.Profile.Value.Rank <= l.Profile!.Value.Rank ? r.ParameterSets : []);
    }
}

internal readonly record struct H264Profile(byte Id, byte Constraints, byte Level, string Group, int Rank)
{
    internal static bool TryRead(string? value, out H264Profile result)
    {
        result = default;
        if (value == null || value.Length != 6 || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits)) return false;
        var id = (byte)(bits >> 16); var c = (byte)(bits >> 8); var level = (byte)bits;
        if ((c & 3) != 0 || level is not (9 or 10 or 11 or 12 or 13 or 20 or 21 or 22 or 30 or 31 or 32 or 40 or 41 or 42 or 50 or 51 or 52 or 60 or 61 or 62) ||
            id is not (0x42 or 0x4D or 0x58 or 0x64 or 0x6E or 0x7A or 0xF4 or 0x2C) || level == 9 && id is 0x42 or 0x4D or 0x58) return false;
        // RFC6184 table5 sub-profile equivalence; additional constraints match exactly.
        var group = id switch
        {
            0x42 when (c & 0x4F) == 0x40 => "CB", 0x4D when (c & 0x8F) == 0x80 => "CB", 0x58 when (c & 0xCF) == 0xC0 => "CB",
            0x42 when (c & 0x4F) == 0 => "B", 0x58 when (c & 0xCF) == 0x80 => "B",
            0x4D when (c & 0xAF) == 0 => "M", 0x58 when (c & 0xCF) == 0 => "E",
            _ => $"{id:x2}{c:x2}",
        };
        var rank = level == 9 || level == 11 && id is 0x42 or 0x4D or 0x58 && (c & 0x10) != 0 ? 105 : level * 10;
        result = new(id, c, level, group, rank); return true;
    }
    internal string AtLevel(int rank)
    {
        var c = Constraints; var level = (byte)(rank / 10);
        if (Id is 0x42 or 0x4D or 0x58)
        { c = (byte)(c & ~0x10); if (rank == 105) { c |= 0x10; level = 11; } }
        else if (rank == 105) level = 9;
        return $"{Id:x2}{c:x2}{level:x2}";
    }
}

internal sealed record VideoParameters(VideoCodec Codec, int Mode, bool Asymmetry, H264Profile? Profile, int? MaxFs, int? MaxFr, string[] ParameterSets)
{
    internal bool Compatible(VideoParameters other) => Codec == other.Codec && Mode == other.Mode &&
        (Codec != VideoCodec.H264 || Profile!.Value.Group == other.Profile!.Value.Group);
    internal static bool TryRead(SdpRtpCodec codec, out VideoParameters result)
    {
        result = null!;
        if (codec.ClockRate != 90000 || codec.Channels != 1 || codec.PayloadType is < 96 or > 127) return false;
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in codec.FormatParameters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equal = part.IndexOf('=');
            if (equal < 1 || !SdpSessionDescription.Token(part[..equal].Trim(), 64) || !SdpSessionDescription.Token(part[(equal + 1)..].Trim(), 1024) ||
                p.Count == 32 || !p.TryAdd(part[..equal].Trim(), part[(equal + 1)..].Trim())) return false;
        }
        if (codec.Name.Equals("VP8", StringComparison.OrdinalIgnoreCase))
        {
            if (!Positive(p, "max-fs", out var fs) || !Positive(p, "max-fr", out var fr)) return false;
            result = new(VideoCodec.Vp8, 0, false, null, fs, fr, []); return true;
        }
        if (!codec.Name.Equals("H264", StringComparison.OrdinalIgnoreCase) ||
            !H264Profile.TryRead(p.GetValueOrDefault("profile-level-id") ?? "42000a", out var profile) ||
            !Flag(p, "packetization-mode", out var mode) || !Flag(p, "level-asymmetry-allowed", out var asymmetry)) return false;
        // Interleaved/source-level parameter-set signaling is a separate negotiation gate.
        if (p.ContainsKey("sprop-level-parameter-sets") || p.GetValueOrDefault("use-level-src-parameter-sets") is not (null or "0") || p.ContainsKey("max-recv-level")) return false;
        foreach (var key in new[] { "max-mbps", "max-smbps", "max-fs", "max-cpb", "max-dpb", "max-br", "max-rcmd-nalu-size" })
            if (!Positive(p, key, out _)) return false;
        string[] sets = [];
        if (p.TryGetValue("sprop-parameter-sets", out var encoded))
        {
            sets = encoded.Split(',');
            if (sets.Length is < 1 or > 16) return false;
            Span<byte> bytes = stackalloc byte[768];
            foreach (var set in sets)
            {
                if (!Convert.TryFromBase64String(set, bytes, out var n) || n < 2 || (bytes[0] & 128) != 0 || (bytes[0] & 31) is not (7 or 8)) return false;
                if ((bytes[0] & 31) == 7 && (n < 4 || !H264Profile.TryRead(Convert.ToHexString(bytes[1..4]), out var sp) || sp.Group != profile.Group || sp.Rank != profile.Rank)) return false;
            }
        }
        result = new(VideoCodec.H264, mode, asymmetry != 0, profile, null, null, sets); return true;
    }
    private static bool Flag(Dictionary<string, string> p, string key, out int n)
    { n = 0; if (!p.TryGetValue(key, out var value)) return true; return value is "0" or "1" && int.TryParse(value, out n); }
    private static bool Positive(Dictionary<string, string> p, string key, out int? n)
    { n = null; if (!p.TryGetValue(key, out var value)) return true; if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i < 1) return false; n = i; return true; }
}

public static partial class SdpNegotiation
{
    internal static SdpRtpCodec[] VideoCapabilities(IEnumerable<VideoCodecCapability>? capabilities)
    {
        var values = capabilities?.Take(9).ToArray() ?? [];
        if (values.Length > 8 || values.Any(v => v == null)) throw new ArgumentException("At most eight explicit video capabilities are supported.", nameof(capabilities));
        var codecs = values.Select(c => c.ToCodec()).ToArray();
        if (codecs.Select(c => c.PayloadType).Distinct().Count() != codecs.Length) throw new ArgumentException("Video payload types must be distinct.", nameof(capabilities));
        return codecs;
    }
    private static SdpRtpCodec? SelectVideo(SdpMediaDescription media, SdpRtpCodec[] local)
    {
        foreach (var capability in local)
            foreach (var codec in media.Codecs)
                if (VideoParameters.TryRead(capability, out var l) && VideoParameters.TryRead(codec, out var r) && l.Compatible(r))
                {
                    var parameters = capability.FormatParameters;
                    if (l.Codec == VideoCodec.H264)
                    {
                        var rank = l.Asymmetry && r.Asymmetry ? l.Profile!.Value.Rank : Math.Min(l.Profile!.Value.Rank, r.Profile!.Value.Rank);
                        parameters = $"profile-level-id={l.Profile!.Value.AtLevel(rank)};packetization-mode={l.Mode};level-asymmetry-allowed={(l.Asymmetry && r.Asymmetry ? 1 : 0)}";
                    }
                    return capability with { PayloadType = codec.PayloadType, FormatParameters = parameters };
                }
        return null;
    }
    private static void WriteVideo(StringBuilder builder, SdpLocalTransport transport, string mid, SdpRtpCodec[] codecs,
        SdpDirection direction, SdpSetup setup, uint source, int extension)
    {
        builder.Append(CultureInfo.InvariantCulture, $"m=video 9 UDP/TLS/RTP/SAVPF {string.Join(' ', codecs.Select(c => c.PayloadType))}\r\nc=IN IP4 0.0.0.0\r\na=mid:{mid}\r\na=rtcp-mux\r\n");
        foreach (var codec in codecs) builder.Append(CultureInfo.InvariantCulture, $"a=rtpmap:{codec.PayloadType} {codec.Name}/90000\r\na=fmtp:{codec.PayloadType} {codec.FormatParameters}\r\n");
        builder.Append("a=").Append(direction switch { SdpDirection.SendOnly => "sendonly", SdpDirection.ReceiveOnly => "recvonly", SdpDirection.Inactive => "inactive", _ => "sendrecv" }).Append("\r\n");
        if (extension != 0) builder.Append(CultureInfo.InvariantCulture, $"a=extmap:{extension} {MidExtension}\r\n");
        if (direction is SdpDirection.SendOnly or SdpDirection.SendReceive) builder.Append(CultureInfo.InvariantCulture, $"a=ssrc:{source} cname:tryagi\r\na=ssrc:{source} msid:tryagi video\r\na=msid:tryagi video\r\n");
        WriteTransport(builder, transport, setup);
    }
}
