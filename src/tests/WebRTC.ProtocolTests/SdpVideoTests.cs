using System.Net;
using tryAGI.WebRTC;

internal static class SdpVideoTests
{
    internal static VideoCodecCapability H264(string profile = "42e01f", bool asymmetry = false, int mode = 1) => new()
    { Codec = VideoCodec.H264, PayloadType = 102, H264ProfileLevelId = profile, H264PacketizationMode = mode, H264LevelAsymmetryAllowed = asymmetry };
    internal static VideoCodecCapability Vp8() => new() { Codec = VideoCodec.Vp8, PayloadType = 96, Vp8MaximumMacroblocks = 3600, Vp8MaximumFrameRate = 30 };
    private static SdpLocalTransport Transport(int port) => new(new("abcd", new string('a', 22)), new byte[32], new(IPAddress.Loopback, port));
    private static void Check(bool value) { if (!value) throw new IOException("Video SDP assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException) { return; } throw new IOException("Unsafe video SDP accepted."); }
    private static string Offer(VideoCodecCapability cap, SdpDirection direction = SdpDirection.SendReceive) => SdpNegotiation.CreateOffer(Transport(20001), 12, 13, [cap], false, videoDirection: direction);
    private static (SdpNegotiatedSession Left, SdpNegotiatedSession Right, string Answer) Pair(string offer, VideoCodecCapability cap, SdpSetup setup = SdpSetup.Active)
    {
        var o = SdpSessionDescription.Parse(offer);
        var answer = SdpNegotiation.CreateAnswer(o, Transport(20002), 22, 23, [cap], false, setup, videoDirection: SdpDirection.SendReceive);
        var a = SdpSessionDescription.Parse(answer);
        return (SdpNegotiation.ValidateAnswer(o, a, true), SdpNegotiation.ValidateAnswer(o, a, false), answer);
    }
    internal static void Selection()
    {
        foreach (var setup in new[] { SdpSetup.Active, SdpSetup.Passive })
        {
            var offer = SdpNegotiation.CreateOffer(Transport(20001), 12, 13, [Vp8(), H264()], true, videoDirection: SdpDirection.SendReceive);
            var o = SdpSessionDescription.Parse(offer);
            var a = SdpSessionDescription.Parse(SdpNegotiation.CreateAnswer(o, Transport(20002), 22, 23, [H264(), Vp8()], true, setup, videoDirection: SdpDirection.ReceiveOnly));
            var session = SdpNegotiation.ValidateAnswer(o, a, true);
            Check(session.CanSendVideo && !session.CanReceiveVideo && session.VideoFormat is { Codec: VideoCodec.H264, PayloadType: 102 } && session.RemoteData != null);
            Check(session.OutgoingVideoHeaderExtensions[1] == SdpNegotiation.MidExtension && session.RemoteVideo!.Codecs.Count == 1);
            Check(session.DtlsRole == (setup == SdpSetup.Active ? DtlsRole.Server : DtlsRole.Client));
        }
        var vp = Pair(Offer(Vp8()), Vp8());
        Check(vp.Left.VideoFormat is { Codec: VideoCodec.Vp8, RemoteVp8MaximumMacroblocks: 3600, RemoteVp8MaximumFrameRate: 30 });
        var unsupported = SdpSessionDescription.Parse(SdpNegotiation.CreateAnswer(SdpSessionDescription.Parse(Offer(H264("64001f"))), Transport(20002), 22, 23, [Vp8()], false));
        Check(unsupported.Media[1].IsRejected && SdpNegotiation.ValidateAnswer(SdpSessionDescription.Parse(Offer(H264("64001f"))), unsupported, true).VideoFormat == null);
    }
    internal static void Profiles()
    {
        foreach (var profile in new[] { "42e01f", "4d801f", "58c01f" })
            Check(Pair(Offer(H264(profile)), H264("42e014")).Left.VideoFormat!.RemoteReceiveProfileLevelId == "42e014");
        var low = Pair(Offer(H264("42f00b")), H264("42e00b"));
        Check(low.Left.VideoFormat!.LocalReceiveProfileLevelId == "42f00b" && low.Right.VideoFormat!.LocalReceiveProfileLevelId == "42f00b");
        var asym = Pair(Offer(H264("42e01f", true)), H264("42e028", true));
        Check(asym.Left.VideoFormat!.LocalReceiveProfileLevelId == "42e01f" && asym.Left.VideoFormat.RemoteReceiveProfileLevelId == "42e028");
        var symmetric = Pair(Offer(H264("42e01f", true)), H264("42e028"));
        Check(symmetric.Left.VideoFormat!.RemoteReceiveProfileLevelId == "42e01f");
        var defaults = Offer(H264("42001f", mode: 0)).Replace("profile-level-id=42001f;packetization-mode=0;level-asymmetry-allowed=0", "level-asymmetry-allowed=0");
        Check(Pair(defaults, H264("42001f", mode: 0)).Left.VideoFormat!.RemoteReceiveProfileLevelId == "42000a");
    }
    internal static void ParametersAndBounds()
    {
        var text = Offer(H264()).Replace("level-asymmetry-allowed=0", "level-asymmetry-allowed=0;sprop-parameter-sets=Z0LgH4A=,aMA=");
        Check(Pair(text, H264()).Right.VideoFormat!.RemoteH264ParameterSets.Count == 2);
        Check(Pair(text, H264("42e014")).Right.VideoFormat!.RemoteH264ParameterSets.Count == 0);
        foreach (var p in new[] { "profile-level-id=000000", "profile-level-id=42e01f;PROFILE-LEVEL-ID=42e01f", "packetization-mode=2", "level-asymmetry-allowed=2", "sprop-parameter-sets=!", "sprop-parameter-sets=Z0LgFIA=", "max-fs=0" })
        {
            var malformed = Offer(H264()).Replace("profile-level-id=42e01f;packetization-mode=1;level-asymmetry-allowed=0", p);
            Check(Pair(malformed, H264()).Left.VideoFormat == null);
        }
        Reject(() => Offer(H264("bad")));
        Reject(() => Offer(Vp8() with { Vp8MaximumFrameRate = 0 }));
        Reject(() => Offer(H264() with { PayloadType = 111 }));
        Reject(() => SdpNegotiation.CreateOffer(Transport(20001), 12, 13, [Vp8(), Vp8()], false));
    }
    internal static void HostileAnswer()
    {
        var offer = Offer(H264()); var answer = Pair(offer, H264("42e014")).Answer;
        foreach (var unsafeAnswer in new[] { answer.Replace("profile-level-id=42e014", "profile-level-id=42e028"), answer.Replace("packetization-mode=1", "packetization-mode=0"),
            answer.Replace("profile-level-id=42e014", "profile-level-id=640014"), answer.Replace("level-asymmetry-allowed=0", "level-asymmetry-allowed=1"),
            answer.Replace("102", "103"), answer.Replace("a=mid:video", "a=mid:other"),
            answer.Replace("H264/90000", "H264/48000"), answer.Replace("BUNDLE audio video", "BUNDLE audio") })
        {
            // The offer is sendrecv; changing its answer direction alone is valid.
            if (unsafeAnswer == answer) continue;
            Reject(() => SdpNegotiation.ValidateAnswer(SdpSessionDescription.Parse(offer), SdpSessionDescription.Parse(unsafeAnswer), true));
        }
        var recv = Offer(H264(), SdpDirection.ReceiveOnly); var recvAnswer = Pair(recv, H264()).Answer;
        Reject(() => SdpNegotiation.ValidateAnswer(SdpSessionDescription.Parse(recv), SdpSessionDescription.Parse(recvAnswer.Replace("a=sendonly", "a=recvonly")), true));
        // A remote offered PT may collide with Opus even when the local capability uses a safe PT.
        var collision = Offer(Vp8()).Replace("96", "111");
        Reject(() => Pair(collision, Vp8()));
    }
}
