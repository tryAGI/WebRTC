using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;

namespace tryAGI.WebRTC;

public enum SdpDirection { SendReceive, SendOnly, ReceiveOnly, Inactive }
public enum SdpSetup { Active, Passive, ActPass, HoldConnection }
public sealed record SdpRtcpFeedback(byte? PayloadType, string Value);
public sealed record SdpRtpCodec(byte PayloadType, string Name, int ClockRate, int Channels, string FormatParameters);

/// <summary>Bounded candidate syntax. Parsing never resolves a name or opens a socket.</summary>
public sealed class SdpIceCandidate
{
    public string Foundation { get; }
    public int Component { get; }
    public string Protocol { get; }
    public uint Priority { get; }
    public string Address { get; }
    public int Port { get; }
    public IceCandidateType Type { get; }
    internal SdpIceCandidate(string foundation, int component, string protocol, uint priority, string address, int port, IceCandidateType type)
    { Foundation = foundation; Component = component; Protocol = protocol; Priority = priority; Address = address; Port = port; Type = type; }
    public IceCandidate? GetResolvedUdpCandidate()
    {
        if (Component != 1 || !Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(Address, out var address)) return null;
        return new(new(address, Port), Priority, Type);
    }
    internal static SdpIceCandidate Parse(string value)
    {
        var fields = SdpSessionDescription.Fields(value);
        if (fields.Length is < 8 or > 40 || (fields.Length - 8) % 2 != 0 || !SdpSessionDescription.Token(fields[0], 32) || fields[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/')) ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var component) || component is < 1 or > 256 ||
            !SdpSessionDescription.Token(fields[2], 16) ||
            !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var priority) || priority is 0 or > int.MaxValue ||
            !SdpSessionDescription.Token(fields[4], 256) ||
            !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535 ||
            fields[6] != "typ") throw SdpSessionDescription.Invalid();
        var type = fields[7] switch { "host" => IceCandidateType.Host, "srflx" => IceCandidateType.ServerReflexive,
            "prflx" => IceCandidateType.PeerReflexive, "relay" => IceCandidateType.Relay, _ => throw SdpSessionDescription.Invalid() };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 8; i < fields.Length; i += 2)
            if (!SdpSessionDescription.Token(fields[i], 64) || !SdpSessionDescription.Token(fields[i + 1], 256) || !keys.Add(fields[i])) throw SdpSessionDescription.Invalid();
        // An IP address is checked now; a hostname remains syntax only.
        if (IPAddress.TryParse(fields[4], out var ip)) _ = new IceCandidate(new(ip, port), priority, type);
        return new(fields[0], component, fields[2], priority, fields[4], port, type);
    }
}

/// <summary>One immutable media description; transport fields have resolved session inheritance.</summary>
public sealed class SdpMediaDescription
{
    public string Kind { get; }
    public int Port { get; }
    public string Protocol { get; }
    public string Mid { get; }
    public bool BundleOnly { get; }
    public bool IsRejected => Port == 0 && !BundleOnly;
    public SdpDirection Direction { get; }
    public bool RtcpMux { get; }
    public bool ReducedSizeRtcp { get; }
    public IReadOnlyList<SdpRtcpFeedback> RtcpFeedback { get; }
    public bool SupportsPictureLoss(byte payloadType) => RtcpFeedback.Any(f => (f.PayloadType == null || f.PayloadType == payloadType) && f.Value == "nack pli");
    public IceCredentials? IceCredentials { get; }
    public string? FingerprintSha256 { get; }
    public SdpSetup? Setup { get; }
    public IReadOnlyList<string> Formats { get; }
    public IReadOnlyList<SdpRtpCodec> Codecs { get; }
    public IReadOnlyList<SdpIceCandidate> Candidates { get; }
    public IReadOnlyDictionary<int, string> HeaderExtensions { get; }
    public IReadOnlyDictionary<int, SdpDirection> HeaderExtensionDirections { get; }
    public IReadOnlyList<uint> Sources { get; }
    public ushort? SctpPort { get; }
    public ulong MaximumMessageSize { get; }
    internal SdpMediaDescription(SdpSessionDescription.Section section, SdpSessionDescription.Section session)
    {
        Kind = section.Kind; Port = section.Port; Protocol = section.Protocol; Mid = section.Mid ?? throw SdpSessionDescription.Invalid();
        BundleOnly = section.BundleOnly; Direction = section.Direction ?? session.Direction ?? SdpDirection.SendReceive;
        RtcpMux = section.RtcpMux; ReducedSizeRtcp = section.RtcpReducedSize;
        RtcpFeedback = Array.AsReadOnly(section.Feedback.ToArray());
        if (!IsRejected && !section.Connection && !session.Connection) throw SdpSessionDescription.Invalid();
        var fragment = section.Fragment ?? session.Fragment; var password = section.Password ?? session.Password;
        if ((fragment == null) != (password == null)) throw SdpSessionDescription.Invalid();
        IceCredentials = fragment == null ? null : new(fragment, password!);
        FingerprintSha256 = section.Fingerprint ?? session.Fingerprint; Setup = section.Setup ?? session.Setup;
        Formats = Array.AsReadOnly(section.Formats.ToArray());
        Codecs = Array.AsReadOnly(section.Codecs.Values.Select(codec =>
            codec with { FormatParameters = section.Fmtp.GetValueOrDefault(codec.PayloadType) ?? "" }).ToArray());
        foreach (var pt in section.Fmtp.Keys) if (!section.Codecs.ContainsKey(pt)) throw SdpSessionDescription.Invalid();
        Candidates = Array.AsReadOnly(section.Candidates.ToArray());
        HeaderExtensions = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>(section.Extensions));
        HeaderExtensionDirections = new ReadOnlyDictionary<int, SdpDirection>(new Dictionary<int, SdpDirection>(section.ExtensionDirections));
        Sources = Array.AsReadOnly(section.Sources.ToArray());
        SctpPort = section.SctpPort; MaximumMessageSize = section.MessageSize ?? 65536;
    }
    public override string ToString() => $"SDP {Kind} MID {Mid} (transport credentials redacted)";
}

/// <summary>Strict bounded WebRTC SDP syntax, independent of transport side effects. Not a complete JSEP state machine.</summary>
public sealed class SdpSessionDescription
{
    public IReadOnlyList<SdpMediaDescription> Media { get; }
    public IReadOnlyList<string> BundleMids { get; }
    public bool IceLite { get; }
    private SdpSessionDescription(Section session, List<Section> sections, string[] bundle, bool iceLite)
    {
        Media = Array.AsReadOnly(sections.Select(s => new SdpMediaDescription(s, session)).ToArray());
        if (Media.Select(m => m.Mid).Distinct(StringComparer.Ordinal).Count() != Media.Count ||
            bundle.Distinct(StringComparer.Ordinal).Count() != bundle.Length || bundle.Any(mid => !Media.Any(m => m.Mid == mid)) ||
            Media.Any(m => m.BundleOnly && (m.Port != 0 || !bundle.Contains(m.Mid)))) throw Invalid();
        BundleMids = Array.AsReadOnly(bundle); IceLite = iceLite;
    }
    public override string ToString() => $"SDP {Media.Count} media sections (transport credentials redacted)";
    internal static FormatException Invalid() => new("Invalid or unsupported SDP field structure.");
    internal static bool Token(string value, int maximum = 256) =>
        value.Length > 0 && value.Length <= maximum && value.All(c => c is >= '!' and <= '~');
    internal static string[] Fields(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static SdpSessionDescription Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 65536 || Utf8.GetByteCount(text) > 65536) throw Invalid();
        var lines = text.Split('\n');
        if (lines.Length > 1024) throw Invalid();
        var session = new Section(); var current = session; var sections = new List<Section>(8);
        string[] bundle = []; var groupSeen = false; var iceLite = false;
        var version = false; var origin = false; var name = false; var time = false;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0) { if (lineIndex != lines.Length - 1 || !time) throw Invalid(); continue; }
            if (line.Length > 2048 || line.Length < 2 || line[1] != '=' || line.Any(c => char.IsControl(c))) throw Invalid();
            if (lineIndex == 0 && line != "v=0") throw Invalid();
            var value = line[2..];
            switch (line[0])
            {
                case 'v': if (version || origin || current != session || value != "0") throw Invalid(); version = true; break;
                case 'o':
                    if (!version || origin || name || current != session) throw Invalid();
                    var fields = Fields(value);
                    if (fields.Length != 6 || !Token(fields[0]) || !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                        !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out _) || fields[3] != "IN" ||
                        fields[4] is not ("IP4" or "IP6") || !Token(fields[5])) throw Invalid();
                    origin = true; break;
                case 's': if (!origin || name || time || current != session || value.Length == 0) throw Invalid(); name = true; break;
                case 't':
                    if (!name || time || current != session || value != "0 0") throw Invalid(); time = true; break;
                case 'm':
                    if (!time || sections.Count == 8) throw Invalid();
                    var media = Fields(value);
                    if (media.Length is < 4 or > 131 || !Token(media[0], 32) || !int.TryParse(media[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                        port is < 0 or > 65535 || !Token(media[2], 64) || media.Skip(3).Any(f => !Token(f, 64)) ||
                        media.Skip(3).Distinct(StringComparer.Ordinal).Count() != media.Length - 3) throw Invalid();
                    // An RTP media inventory is bounded by its complete 7-bit payload type space.
                    // Preserve generic tokens for non-RTP formats such as webrtc-datachannel.
                    if (media[2].Split('/').Contains("RTP", StringComparer.Ordinal))
                    {
                        var payloads = new HashSet<byte>();
                        foreach (var format in media.Skip(3))
                            if (!byte.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out var payload) ||
                                payload > 127 || !payloads.Add(payload)) throw Invalid();
                    }
                    current = new() { Kind = media[0], Port = port, Protocol = media[2] };
                    current.Formats.AddRange(media.Skip(3)); sections.Add(current); break;
                case 'a':
                    if (!time) throw Invalid();
                    if (++current.Attributes > (current.Kind == "video" ? 512 : 128)) throw Invalid();
                    var colon = value.IndexOf(':'); var key = colon < 0 ? value : value[..colon]; var body = colon < 0 ? "" : value[(colon + 1)..];
                    if (!Token(key, 64) || body.Length > 1024) throw Invalid();
                    switch (key)
                    {
                        case "group":
                            if (current != session) throw Invalid();
                            var ids = Fields(body);
                            if (ids.Length == 0) throw Invalid();
                            if (ids[0] != "BUNDLE") break;
                            if (groupSeen || ids.Length is < 2 or > 9 || ids.Skip(1).Any(id => !Token(id, 32))) throw Invalid();
                            groupSeen = true; bundle = ids[1..]; break;
                        case "ice-lite":
                            if (current != session || iceLite || colon >= 0) throw Invalid(); iceLite = true; break;
                        case "mid":
                            if (current == session || current.Mid != null || !Token(body, 32)) throw Invalid(); current.Mid = body; break;
                        case "ice-ufrag": Unique(ref current.Fragment, body); break;
                        case "ice-pwd": Unique(ref current.Password, body); break;
                        case "fingerprint":
                            var fingerprint = Fields(body);
                            if (fingerprint.Length != 2 || !fingerprint[0].Equals("sha-256", StringComparison.OrdinalIgnoreCase)) throw Invalid();
                            var bytes = fingerprint[1].Split(':');
                            if (bytes.Length != 32 || bytes.Any(b => b.Length != 2 || !byte.TryParse(b, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))) throw Invalid();
                            Unique(ref current.Fingerprint, string.Concat(bytes).ToUpperInvariant()); break;
                        case "setup":
                            if (current.Setup != null) throw Invalid();
                            current.Setup = body switch { "active" => SdpSetup.Active, "passive" => SdpSetup.Passive,
                                "actpass" => SdpSetup.ActPass, "holdconn" => SdpSetup.HoldConnection, _ => throw Invalid() }; break;
                        case "sendrecv": case "sendonly": case "recvonly": case "inactive":
                            if (colon >= 0 || current.Direction != null) throw Invalid();
                            current.Direction = key switch { "sendrecv" => SdpDirection.SendReceive, "sendonly" => SdpDirection.SendOnly,
                                "recvonly" => SdpDirection.ReceiveOnly, _ => SdpDirection.Inactive }; break;
                        case "rtcp-mux": if (current == session || current.RtcpMux || colon >= 0) throw Invalid(); current.RtcpMux = true; break;
                        case "rtcp-rsize":
                            if (current == session || current.RtcpReducedSize || colon >= 0 || current.Kind is not ("audio" or "video")) throw Invalid();
                            current.RtcpReducedSize = true; break;
                        case "rtcp-fb":
                            var separator = body.IndexOf(' '); byte? feedbackPayload = null;
                            if (current == session || separator <= 0 || current.Feedback.Count == 128 || !current.Protocol.EndsWith("AVPF", StringComparison.Ordinal)) throw Invalid();
                            if (body[..separator] != "*")
                            {
                                if (!byte.TryParse(body.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var feedbackPt) || feedbackPt > 127 ||
                                    !current.Formats.Contains(feedbackPt.ToString(CultureInfo.InvariantCulture))) throw Invalid();
                                feedbackPayload = feedbackPt;
                            }
                            var feedbackValue = body[(separator + 1)..];
                            if (feedbackValue.Length is < 1 or > 256 || feedbackValue[0] == ' ' || feedbackValue[^1] == ' ' || feedbackValue.Any(c => c is < ' ' or > '~')) throw Invalid();
                            var feedback = new SdpRtcpFeedback(feedbackPayload, feedbackValue);
                            if (current.Feedback.Contains(feedback)) throw Invalid(); current.Feedback.Add(feedback); break;
                        case "bundle-only": if (current == session || current.BundleOnly || colon >= 0) throw Invalid(); current.BundleOnly = true; break;
                        case "candidate":
                            if (current == session || current.Candidates.Count == 64) throw Invalid(); current.Candidates.Add(SdpIceCandidate.Parse(body)); break;
                        case "rtpmap": ParseCodec(current, body); break;
                        case "fmtp":
                            var space = body.IndexOf(' ');
                            if (current == session || space <= 0 || !byte.TryParse(body.AsSpan(0, space), NumberStyles.None, CultureInfo.InvariantCulture, out var pt) || pt > 127 ||
                                !current.Formats.Contains(pt.ToString(CultureInfo.InvariantCulture)) || !current.Fmtp.TryAdd(pt, body[(space + 1)..])) throw Invalid();
                            break;
                        case "extmap":
                            var extension = Fields(body); var extId = extension.Length == 0 ? [] : extension[0].Split('/');
                            if (current == session || extension.Length < 2 || extId.Length is < 1 or > 2 ||
                                !int.TryParse(extId[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id is < 1 or > 255 ||
                                (extId.Length == 2 && extId[1] is not ("sendrecv" or "sendonly" or "recvonly" or "inactive")) ||
                                !Token(extension[1]) || current.Extensions.Count == 32 || !current.Extensions.TryAdd(id, extension[1])) throw Invalid();
                            current.ExtensionDirections[id] = extId.Length == 1 ? SdpDirection.SendReceive : extId[1] switch { "sendonly" => SdpDirection.SendOnly, "recvonly" => SdpDirection.ReceiveOnly, "inactive" => SdpDirection.Inactive, _ => SdpDirection.SendReceive }; break;
                        case "ssrc":
                            var source = Fields(body);
                            if (current == session || source.Length < 2 || !uint.TryParse(source[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ssrc) ||
                                ssrc == 0 || current.Sources.Count == 64 && !current.Sources.Contains(ssrc)) throw Invalid();
                            current.Sources.Add(ssrc); break;
                        case "sctp-port":
                            if (current == session || current.SctpPort != null || !ushort.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var sctpPort) || sctpPort == 0) throw Invalid();
                            current.SctpPort = sctpPort; break;
                        case "max-message-size":
                            if (current == session || current.MessageSize != null || !ulong.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var size)) throw Invalid();
                            current.MessageSize = size; break;
                        case "crypto": throw Invalid(); // SDES keys cannot replace DTLS authentication.
                        default: if (++current.UnknownAttributes > 128) throw Invalid(); break;
                    }
                    break;
                case 'c':
                    var connection = Fields(value);
                    if (current.Connection || connection.Length != 3 || connection[0] != "IN" || connection[1] is not ("IP4" or "IP6") || !Token(connection[2])) throw Invalid(); current.Connection = true; break;
                case 'k': throw Invalid(); // SDES/plaintext key material is outside the authenticated DTLS subset.
                case 'i': case 'u': case 'e': case 'p': case 'b': break;
                default: throw Invalid();
            }
        }
        if (!version || !origin || !name || !time || sections.Count == 0) throw Invalid();
        return new(session, sections, bundle, iceLite);
    }
    private static void Unique(ref string? field, string value)
    { if (field != null || !Token(value, 256)) throw Invalid(); field = value; }
    private static void ParseCodec(Section current, string value)
    {
        var fields = Fields(value);
        if (current.Kind is not ("audio" or "video") || fields.Length != 2 ||
            !byte.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pt) || pt > 127 || !current.Formats.Contains(fields[0])) throw Invalid();
        var format = fields[1].Split('/');
        if (format.Length is < 2 or > 3 || !Token(format[0], 64) ||
            !int.TryParse(format[1], NumberStyles.None, CultureInfo.InvariantCulture, out var clock) || clock is < 1 or > 192000) throw Invalid();
        var channels = 1;
        if (format.Length == 3 && (!int.TryParse(format[2], NumberStyles.None, CultureInfo.InvariantCulture, out channels) || channels is < 1 or > 64)) throw Invalid();
        if (!current.Codecs.TryAdd(pt, new(pt, format[0], clock, channels, ""))) throw Invalid();
    }
    internal sealed class Section
    {
        internal string Kind = "", Protocol = "";
        internal int Port, Attributes, UnknownAttributes;
        internal string? Mid, Fragment, Password, Fingerprint;
        internal SdpSetup? Setup;
        internal SdpDirection? Direction;
        internal bool RtcpMux, RtcpReducedSize, BundleOnly, Connection;
        internal readonly List<SdpRtcpFeedback> Feedback = [];
        internal ushort? SctpPort;
        internal ulong? MessageSize;
        internal readonly List<string> Formats = [];
        internal readonly Dictionary<byte, SdpRtpCodec> Codecs = [];
        internal readonly Dictionary<byte, string> Fmtp = [];
        internal readonly Dictionary<int, string> Extensions = [];
        internal readonly Dictionary<int, SdpDirection> ExtensionDirections = [];
        internal readonly List<SdpIceCandidate> Candidates = [];
        internal readonly HashSet<uint> Sources = [];
    }
}
