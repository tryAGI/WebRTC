using System.Net;
using tryAGI.WebRTC;

internal static class SdpRtcpTests
{
    private static SdpLocalTransport Transport(string name = "stable-cname") => new(new("abcd", new string('a', 22)), new byte[32], new(IPAddress.Loopback, 20001), canonicalName: name);
    private static void Check(bool value) { if (!value) throw new IOException("RTCP SDP assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException) { return; } throw new IOException("Unsafe RTCP SDP accepted."); }
    private static string Offer() => SdpNegotiation.CreateOffer(Transport(), 12, 13, [SdpVideoTests.Vp8(), SdpVideoTests.H264()], true, videoDirection: SdpDirection.SendReceive);
    private static string Answer(string offer) => SdpNegotiation.CreateAnswer(SdpSessionDescription.Parse(offer), Transport("answer-name"), 22, 23, [SdpVideoTests.H264()], true, videoDirection: SdpDirection.SendReceive);
    private static SdpNegotiatedSession Pair(string offer, string answer, bool offerer = true) => SdpNegotiation.ValidateAnswer(SdpSessionDescription.Parse(offer), SdpSessionDescription.Parse(answer), offerer);
    internal static void Negotiation()
    {
        var offer = Offer(); var answer = Answer(offer);
        foreach (var role in new[] { false, true }) Check(Pair(offer, answer, role) is { VideoPictureLoss: true, AudioReducedSizeRtcp: true, VideoReducedSizeRtcp: true });
        Check(offer.Split("cname:stable-cname").Length == 3 && answer.Split("cname:answer-name").Length == 3);
        var noRsize = offer.Replace("a=rtcp-rsize\r\n", "");
        Check(Pair(noRsize, Answer(noRsize)) is { AudioReducedSizeRtcp: false, VideoReducedSizeRtcp: false });
        Reject(() => Pair(noRsize, answer));
        var providerWhitespace = offer.Replace("a=rtcp-fb:102 nack pli\r\n", "a=rtcp-fb:102 nack pli \r\n");
        Check(Pair(providerWhitespace, Answer(providerWhitespace)).VideoPictureLoss);
        var noPli = offer.Replace("a=rtcp-fb:102 nack pli\r\n", "");
        Check(!Pair(noPli, Answer(noPli)).VideoPictureLoss);
        Reject(() => Pair(noPli, answer));
        var wildcardOffer = offer.Replace("a=rtcp-fb:96 nack pli\r\na=rtcp-fb:102 nack pli", "a=rtcp-fb:* nack pli");
        Check(Pair(wildcardOffer, Answer(wildcardOffer)).VideoPictureLoss);
        // A wildcard answer applies only to its selected format, not to rejected codecs.
        var wildcardAnswer = answer.Replace("a=rtcp-fb:102 nack pli", "a=rtcp-fb:* nack pli");
        Check(Pair(offer, wildcardAnswer).VideoPictureLoss);
        Reject(() => Pair(noPli, wildcardAnswer));
        Reject(() => Pair(offer, answer.Replace("nack pli", "nack")));
        Reject(() => Pair(offer, answer.Replace("nack pli", "ccm fir")));
        var removed = answer.Replace("a=rtcp-rsize\r\n", "").Replace("a=rtcp-fb:102 nack pli\r\n", "");
        Check(Pair(offer, removed) is { AudioReducedSizeRtcp: false, VideoReducedSizeRtcp: false, VideoPictureLoss: false });
    }
    internal static void Bounds()
    {
        var offer = Offer();
        foreach (var name in new[] { "", "with space", "newline\r\n", new string('a', 129) }) Reject(() => Transport(name));
        Check(Transport().CanonicalName == "stable-cname" && Transport(new string('a', 128)).CanonicalName.Length == 128);
        var unknown = offer.Replace("a=rtcp-fb:102 nack pli", "a=rtcp-fb:102 nack pli\r\na=rtcp-fb:* future feedback");
        var parsed = SdpSessionDescription.Parse(unknown).Media[1];
        Check(parsed.RtcpFeedback.Count == 3 && parsed.RtcpFeedback.Any(f => f.Value == "future feedback"));
        try { ((IList<SdpRtcpFeedback>)parsed.RtcpFeedback)[0] = new(null, "bad"); throw new IOException("Mutable SDP feedback"); } catch (NotSupportedException) { }
        foreach (var value in new[] { "102", "102 ", "102  nack pli", "128 nack pli", "97 nack pli", "102 bad\tvalue", "102 " + new string('a', 257) })
            Reject(() => SdpSessionDescription.Parse(offer.Replace("102 nack pli", value)));
        Reject(() => SdpSessionDescription.Parse(offer.Replace("a=rtcp-rsize", "a=rtcp-rsize\r\na=rtcp-rsize")));
        Reject(() => SdpSessionDescription.Parse(offer.Replace("a=rtcp-fb:102 nack pli", "a=rtcp-fb:102 nack pli\r\na=rtcp-fb:102 nack pli")));
        Reject(() => SdpSessionDescription.Parse(offer.Replace("a=group:BUNDLE", "a=rtcp-fb:* nack pli\r\na=group:BUNDLE")));
        Reject(() => SdpSessionDescription.Parse(offer.Replace("a=sctp-port:5000", "a=rtcp-rsize\r\na=sctp-port:5000")));
        var many = string.Join("\r\n", Enumerable.Range(0, 128).Select(i => $"a=rtcp-fb:* future{i}"));
        Reject(() => SdpSessionDescription.Parse(offer.Replace("a=rtcp-fb:102 nack pli", many)));
    }
}
