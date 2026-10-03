using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace tryAGI.WebRTC;

/// <summary>One local resolved host base, optional gathered srflx/relay candidates and authenticated signaling material. Does not perform I/O.</summary>
public sealed class SdpLocalTransport
{
    public IceCredentials Credentials { get; }
    public string FingerprintSha256 { get; }
    public IceCandidate Candidate { get; }
    public IReadOnlyList<IceCandidate> Candidates { get; }
    public bool GatheringComplete { get; }
    public bool SupportsTrickle { get; }
    public ushort SctpPort { get; }
    public int MaximumMessageSize { get; }
    public SdpLocalTransport(IceCredentials credentials, ReadOnlySpan<byte> fingerprintSha256, IPEndPoint candidate,
        ushort sctpPort = 5000, int maximumMessageSize = 262144,
        IEnumerable<IceCandidate>? additionalCandidates = null, bool gatheringComplete = true, bool supportsTrickle = false, bool relayOnly = false)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (fingerprintSha256.Length != 32 || sctpPort == 0 || maximumMessageSize is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(fingerprintSha256));
        Credentials = credentials; FingerprintSha256 = Convert.ToHexString(fingerprintSha256);
        Candidate = new(candidate); SctpPort = sctpPort; MaximumMessageSize = maximumMessageSize;
        var extras = additionalCandidates?.Take(9).ToArray() ?? [];
        if (extras.Length > 8 || extras.Any(c => c == null || c.EndPoint.AddressFamily != Candidate.EndPoint.AddressFamily ||
            c.RelatedEndPoint == null || c.Type is not (IceCandidateType.ServerReflexive or IceCandidateType.Relay) ||
            c.Type == IceCandidateType.ServerReflexive && !c.RelatedEndPoint.Equals(Candidate.EndPoint)))
            throw new ArgumentException("Additional local candidates must be at most eight same-family srflx mappings or relay allocations with their related base.", nameof(additionalCandidates));
        Candidates = Array.AsReadOnly(new[] { Candidate }.Concat(extras).Where(c => !relayOnly || c.Type == IceCandidateType.Relay)
            .DistinctBy(c => c.EndPoint.ToString()).ToArray());
        GatheringComplete = gatheringComplete;
        SupportsTrickle = supportsTrickle;
    }
    public override string ToString() => "SDP local transport (credentials redacted)";
}

public sealed class SdpNegotiatedSession
{
    public SdpMediaDescription? LocalAudio { get; }
    public SdpMediaDescription? RemoteAudio { get; }
    public SdpMediaDescription? LocalData { get; }
    public SdpMediaDescription? RemoteData { get; }
    public SdpRtpCodec? AudioCodec { get; }
    public IceCredentials RemoteCredentials { get; }
    public string RemoteFingerprintSha256 { get; }
    public DtlsRole DtlsRole { get; }
    public IceRole IceRole { get; }
    public SdpDirection AudioDirection { get; }
    public IReadOnlyDictionary<int, string> OutgoingAudioHeaderExtensions { get; }
    public IReadOnlyDictionary<int, string> IncomingAudioHeaderExtensions { get; }
    public bool CanSendAudio => LocalAudio != null && AudioDirection is SdpDirection.SendReceive or SdpDirection.SendOnly;
    public bool CanReceiveAudio => LocalAudio != null && AudioDirection is SdpDirection.SendReceive or SdpDirection.ReceiveOnly;
    public int MaximumMessageSize { get; }
    public IReadOnlyList<SdpIceCandidate> RemoteCandidates { get; }
    internal SdpNegotiatedSession(SdpSessionDescription local, SdpSessionDescription remote, SdpMediaDescription? localAudio,
        SdpMediaDescription? remoteAudio, SdpMediaDescription? localData, SdpMediaDescription? remoteData, SdpRtpCodec? codec, bool localOfferer)
    {
        var answeredDirection = (localOfferer ? remoteAudio : localAudio)?.Direction ?? SdpDirection.Inactive;
        AudioDirection = !localOfferer ? answeredDirection : answeredDirection switch { SdpDirection.SendOnly => SdpDirection.ReceiveOnly, SdpDirection.ReceiveOnly => SdpDirection.SendOnly, _ => answeredDirection };
        var answeredAudio = localOfferer ? remoteAudio : localAudio;
        OutgoingAudioHeaderExtensions = new ReadOnlyDictionary<int, string>(answeredAudio == null ? new() :
            answeredAudio.HeaderExtensions.Where(e => answeredAudio.HeaderExtensionDirections[e.Key] == SdpDirection.SendReceive ||
                answeredAudio.HeaderExtensionDirections[e.Key] == (localOfferer ? SdpDirection.ReceiveOnly : SdpDirection.SendOnly)).ToDictionary(e => e.Key, e => e.Value));
        IncomingAudioHeaderExtensions = new ReadOnlyDictionary<int, string>(answeredAudio == null ? new() :
            answeredAudio.HeaderExtensions.Where(e => answeredAudio.HeaderExtensionDirections[e.Key] == SdpDirection.SendReceive ||
                answeredAudio.HeaderExtensionDirections[e.Key] == (localOfferer ? SdpDirection.SendOnly : SdpDirection.ReceiveOnly)).ToDictionary(e => e.Key, e => e.Value));
        LocalAudio = localAudio; RemoteAudio = remoteAudio; LocalData = localData; RemoteData = remoteData; AudioCodec = codec;
        var transport = remoteAudio ?? remoteData ?? throw new InvalidOperationException("No accepted SDP media.");
        RemoteCredentials = transport.IceCredentials!; RemoteFingerprintSha256 = transport.FingerprintSha256!;
        var setup = (localOfferer ? transport : localAudio ?? localData)!.Setup;
        DtlsRole = localOfferer ? setup == SdpSetup.Active ? DtlsRole.Server : DtlsRole.Client :
            setup == SdpSetup.Active ? DtlsRole.Client : DtlsRole.Server;
        IceRole = localOfferer || remote.IceLite ? IceRole.Controlling : IceRole.Controlled;
        MaximumMessageSize = localData == null ? 0 : (int)Math.Min(1048576UL,
            Math.Min(localData.MaximumMessageSize == 0 ? 1048576UL : localData.MaximumMessageSize,
                     remoteData!.MaximumMessageSize == 0 ? 1048576UL : remoteData.MaximumMessageSize));
        RemoteCandidates = Array.AsReadOnly(new[] { remoteAudio, remoteData }.OfType<SdpMediaDescription>()
            .SelectMany(m => m.Candidates).Take(128).ToArray());
    }
    public override string ToString() => "Negotiated SDP session (credentials redacted)";
}

/// <summary>Initial bundled Opus/data-channel offer-answer subset. Renegotiation, video selection and ICE gathering are separate gates.</summary>
public static class SdpNegotiation
{
    public const string MidExtension = "urn:ietf:params:rtp-hdrext:sdes:mid";
    public static string CreateOpusOffer(SdpLocalTransport transport, uint source, bool dataChannels = true,
        SdpDirection direction = SdpDirection.SendReceive)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (source == 0 || !Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(source));
        var builder = Header(dataChannels ? ["audio", "data"] : ["audio"]);
        WriteAudio(builder, transport, "audio", 111, direction, SdpSetup.ActPass, source, 1);
        if (dataChannels) WriteData(builder, transport, "data", SdpSetup.ActPass);
        return builder.ToString();
    }

    public static string CreateOpusAnswer(SdpSessionDescription offer, SdpLocalTransport transport, uint source, bool dataChannels = true, SdpSetup preferredSetup = SdpSetup.Active,
        SdpDirection direction = SdpDirection.SendReceive)
    {
        ArgumentNullException.ThrowIfNull(offer); ArgumentNullException.ThrowIfNull(transport);
        if (source == 0 || preferredSetup is not (SdpSetup.Active or SdpSetup.Passive) || !Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(source));
        SdpMediaDescription? audio = null, data = null;
        foreach (var media in offer.Media)
        {
            if (media.IsRejected) continue;
            if (audio == null && media.Kind == "audio" && IsRtp(media) && media.RtcpMux && Opus(media) != null) audio = media;
            else if (dataChannels && data == null && IsData(media)) data = media;
        }
        var active = new[] { audio, data }.OfType<SdpMediaDescription>().ToArray();
        if (active.Length == 0) throw new NotSupportedException("The offer has no supported Opus/data media.");
        ValidateTransport(offer, active);
        var setup = active[0].Setup switch { SdpSetup.ActPass => preferredSetup, SdpSetup.Passive => SdpSetup.Active,
            SdpSetup.Active => SdpSetup.Passive, _ => throw Incompatible() };
        var mids = offer.BundleMids.Where(mid => active.Any(m => m.Mid == mid)).ToArray();
        var builder = Header(mids);
        foreach (var media in offer.Media)
        {
            if (ReferenceEquals(media, audio))
                WriteAudio(builder, transport, media.Mid, Opus(media)!.PayloadType, Intersect(Invert(media.Direction), direction), setup, source,
                    media.HeaderExtensions.FirstOrDefault(e => e.Value == MidExtension && media.HeaderExtensionDirections[e.Key] == SdpDirection.SendReceive).Key);
            else if (ReferenceEquals(media, data)) WriteData(builder, transport, media.Mid, setup);
            else builder.Append(CultureInfo.InvariantCulture, $"m={media.Kind} 0 {media.Protocol} {string.Join(' ', media.Formats)}\r\na=mid:{media.Mid}\r\n");
        }
        return builder.ToString();
    }

    public static SdpNegotiatedSession ValidateOpusAnswer(SdpSessionDescription offer, SdpSessionDescription answer, bool localOfferer)
    {
        ArgumentNullException.ThrowIfNull(offer); ArgumentNullException.ThrowIfNull(answer);
        if (offer.Media.Count != answer.Media.Count || offer.IceLite && answer.IceLite) throw Incompatible();
        if ((localOfferer ? offer : answer).IceLite) throw new NotSupportedException("Local ICE-lite is not implemented; remote ICE-lite is supported.");
        SdpMediaDescription? audioOffer = null, audioAnswer = null, dataOffer = null, dataAnswer = null;
        for (var i = 0; i < offer.Media.Count; i++)
        {
            var offered = offer.Media[i]; var accepted = answer.Media[i];
            if (offered.Kind != accepted.Kind || offered.Mid != accepted.Mid || offered.Protocol != accepted.Protocol ||
                accepted.BundleOnly || offered.IsRejected && !accepted.IsRejected) throw Incompatible();
            if (accepted.IsRejected) continue;
            if (accepted.Formats.Any(format => !offered.Formats.Contains(format))) throw Incompatible();
            if (accepted.Kind == "audio")
            {
                var codec = Opus(accepted); var original = codec == null ? null : offered.Codecs.FirstOrDefault(c => c.PayloadType == codec.PayloadType);
                if (audioAnswer != null || !IsRtp(accepted) || !offered.RtcpMux || !accepted.RtcpMux ||
                    codec == null || original == null || !IsOpus(original) || accepted.Codecs.Count != 1 ||
                    accepted.Formats.Count != 1 || !DirectionAllowed(offered.Direction, accepted.Direction)) throw Incompatible();
                foreach (var extension in accepted.HeaderExtensions)
                    if (!offered.HeaderExtensions.TryGetValue(extension.Key, out var uri) || uri != extension.Value ||
                        !DirectionAllowed(offered.HeaderExtensionDirections[extension.Key], accepted.HeaderExtensionDirections[extension.Key])) throw Incompatible();
                audioOffer = offered; audioAnswer = accepted;
            }
            else if (IsData(accepted) && IsData(offered) && dataAnswer == null) { dataOffer = offered; dataAnswer = accepted; }
            else throw Incompatible();
            if (accepted.Setup is not (SdpSetup.Active or SdpSetup.Passive) ||
                offered.Setup == SdpSetup.Active && accepted.Setup != SdpSetup.Passive ||
                offered.Setup == SdpSetup.Passive && accepted.Setup != SdpSetup.Active ||
                offered.Setup is not (SdpSetup.ActPass or SdpSetup.Active or SdpSetup.Passive)) throw Incompatible();
        }
        var offeredActive = new[] { audioOffer, dataOffer }.OfType<SdpMediaDescription>().ToArray();
        var answerActive = new[] { audioAnswer, dataAnswer }.OfType<SdpMediaDescription>().ToArray();
        if (answerActive.Length == 0) throw Incompatible();
        ValidateTransport(offer, offeredActive); ValidateTransport(answer, answerActive);
        if (answer.BundleMids.Any(mid => !offer.BundleMids.Contains(mid) || !answerActive.Any(m => m.Mid == mid))) throw Incompatible();
        return new(localOfferer ? offer : answer, localOfferer ? answer : offer,
            localOfferer ? audioOffer : audioAnswer, localOfferer ? audioAnswer : audioOffer,
            localOfferer ? dataOffer : dataAnswer, localOfferer ? dataAnswer : dataOffer, Opus(audioAnswer), localOfferer);
    }
    private static bool DirectionAllowed(SdpDirection offer, SdpDirection answer) => offer switch
    {
        SdpDirection.SendReceive => true,
        SdpDirection.SendOnly => answer is SdpDirection.ReceiveOnly or SdpDirection.Inactive,
        SdpDirection.ReceiveOnly => answer is SdpDirection.SendOnly or SdpDirection.Inactive,
        _ => answer == SdpDirection.Inactive,
    };
    private static SdpDirection Invert(SdpDirection value) => value switch
    { SdpDirection.SendOnly => SdpDirection.ReceiveOnly, SdpDirection.ReceiveOnly => SdpDirection.SendOnly, _ => value };
    private static SdpDirection Intersect(SdpDirection remote, SdpDirection local)
    {
        var send = remote is SdpDirection.SendReceive or SdpDirection.SendOnly && local is SdpDirection.SendReceive or SdpDirection.SendOnly;
        var receive = remote is SdpDirection.SendReceive or SdpDirection.ReceiveOnly && local is SdpDirection.SendReceive or SdpDirection.ReceiveOnly;
        return send ? receive ? SdpDirection.SendReceive : SdpDirection.SendOnly : receive ? SdpDirection.ReceiveOnly : SdpDirection.Inactive;
    }
    private static bool IsRtp(SdpMediaDescription media) => media.Protocol == "UDP/TLS/RTP/SAVPF";
    private static bool IsData(SdpMediaDescription media) => media.Kind == "application" && media.Protocol == "UDP/DTLS/SCTP" &&
        media.Formats.SequenceEqual(new[] { "webrtc-datachannel" }) && media.SctpPort != null && media.Direction == SdpDirection.SendReceive;
    private static bool IsOpus(SdpRtpCodec codec) => codec.PayloadType is >= 96 and <= 127 && codec.Name.Equals("opus", StringComparison.OrdinalIgnoreCase) && codec.ClockRate == 48000 && codec.Channels == 2;
    private static SdpRtpCodec? Opus(SdpMediaDescription? media) => media?.Codecs.FirstOrDefault(IsOpus);
    private static InvalidOperationException Incompatible() => new("Incompatible or unsupported SDP negotiation.");
    private static void ValidateTransport(SdpSessionDescription description, SdpMediaDescription[] media)
    {
        if (media.Length > 1 && media.Any(m => !description.BundleMids.Contains(m.Mid))) throw Incompatible();
        var first = media[0];
        if (first.IceCredentials == null || first.FingerprintSha256 == null || first.Setup == null) throw Incompatible();
        foreach (var other in media)
            if (other.IceCredentials == null || other.IceCredentials.UsernameFragment != first.IceCredentials.UsernameFragment ||
                other.IceCredentials.Password != first.IceCredentials.Password || other.FingerprintSha256 != first.FingerprintSha256 ||
                other.Setup != first.Setup || other.IsRejected) throw Incompatible();
    }
    private static StringBuilder Header(string[] mids)
    {
        var session = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
        var builder = new StringBuilder().Append(CultureInfo.InvariantCulture, $"v=0\r\no=- {session} 0 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n");
        if (mids.Length != 0) builder.Append($"a=group:BUNDLE {string.Join(' ', mids)}\r\n");
        return builder.Append("a=msid-semantic: WMS *\r\n");
    }
    private static void WriteTransport(StringBuilder builder, SdpLocalTransport transport, SdpSetup setup)
    {
        var fingerprint = string.Join(':', Enumerable.Range(0, 32).Select(i => transport.FingerprintSha256.Substring(2 * i, 2)));
        builder.Append(CultureInfo.InvariantCulture, $"a=ice-ufrag:{transport.Credentials.UsernameFragment}\r\na=ice-pwd:{transport.Credentials.Password}\r\na=fingerprint:sha-256 {fingerprint}\r\na=setup:{(setup == SdpSetup.ActPass ? "actpass" : setup == SdpSetup.Active ? "active" : "passive")}\r\n");
        if (transport.SupportsTrickle) builder.Append("a=ice-options:trickle\r\n");
        WriteCandidates(builder, transport);
    }
    internal static void WriteCandidates(StringBuilder builder, SdpLocalTransport transport)
    {
        for (var i = 0; i < transport.Candidates.Count; i++)
            builder.Append("a=candidate:").Append(transport.Candidates[i].ToSdpAttribute((i + 1).ToString(CultureInfo.InvariantCulture))).Append("\r\n");
        if (transport.GatheringComplete) builder.Append("a=end-of-candidates\r\n");
    }
    private static void WriteAudio(StringBuilder builder, SdpLocalTransport transport, string mid, byte payloadType,
        SdpDirection direction, SdpSetup setup, uint source, int extension)
    {
        builder.Append(CultureInfo.InvariantCulture, $"m=audio 9 UDP/TLS/RTP/SAVPF {payloadType}\r\nc=IN IP4 0.0.0.0\r\na=mid:{mid}\r\na=rtcp-mux\r\na=rtpmap:{payloadType} opus/48000/2\r\na=fmtp:{payloadType} minptime=10;useinbandfec=1\r\n");
        builder.Append("a=").Append(direction switch { SdpDirection.SendOnly => "sendonly", SdpDirection.ReceiveOnly => "recvonly", SdpDirection.Inactive => "inactive", _ => "sendrecv" }).Append("\r\n");
        if (extension != 0) builder.Append(CultureInfo.InvariantCulture, $"a=extmap:{extension} {MidExtension}\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"a=ssrc:{source} cname:tryagi\r\na=ssrc:{source} msid:tryagi audio\r\na=msid:tryagi audio\r\n");
        WriteTransport(builder, transport, setup);
    }
    private static void WriteData(StringBuilder builder, SdpLocalTransport transport, string mid, SdpSetup setup)
    {
        builder.Append(CultureInfo.InvariantCulture, $"m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\na=mid:{mid}\r\na=sctp-port:{transport.SctpPort}\r\na=max-message-size:{transport.MaximumMessageSize}\r\n");
        WriteTransport(builder, transport, setup);
    }
}
