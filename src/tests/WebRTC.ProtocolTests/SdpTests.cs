using System.Collections.Generic;
using System.Net;
using tryAGI.WebRTC;

internal static class SdpTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("SDP assertion failed"); }
    private static readonly byte[] Fingerprint = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static SdpLocalTransport Transport(int port = 49152) => new(new("local012", new string('a', 22)), Fingerprint, new(IPAddress.Loopback, port));
    private static string Offer(SdpDirection direction = SdpDirection.SendReceive, bool data = true) => SdpNegotiation.CreateOpusOffer(Transport(), 1234, data, direction);
    private static void Reject(string text)
    { try { SdpSessionDescription.Parse(text); throw new InvalidOperationException("Malformed SDP accepted"); } catch (FormatException) { } catch (ArgumentException) { } }
    private static void RejectAnswer(string offer, string answer)
    {
        try { SdpNegotiation.ValidateOpusAnswer(SdpSessionDescription.Parse(offer), SdpSessionDescription.Parse(answer), true); throw new IOException("Unsafe answer accepted"); }
        catch (FormatException) { } catch (ArgumentException) { } catch (InvalidOperationException error) when (error.Message == "Incompatible or unsupported SDP negotiation.") { }
    }
    internal static void Syntax()
    {
        var text = Offer(); var parsed = SdpSessionDescription.Parse(text);
        Check(parsed.Media.Count == 2 && parsed.BundleMids.SequenceEqual(new[] { "audio", "data" }));
        Check(parsed.Media[0].Codecs.Single() is { Name: "opus", ClockRate: 48000, Channels: 2 });
        Check(parsed.Media[0].Candidates.Single().GetResolvedUdpCandidate()!.EndPoint.Port == 49152);
        Check(parsed.Media[1].SctpPort == 5000 && parsed.Media[1].MaximumMessageSize == 262144);
        Check(!parsed.ToString().Contains(new string('a', 22)) && !parsed.Media[0].ToString().Contains(new string('a', 22)));
        try { ((IList<SdpMediaDescription>)parsed.Media)[0] = parsed.Media[1]; throw new IOException("Mutable media inventory"); } catch (NotSupportedException) { }
        var fingerprint = string.Join(':', Fingerprint.Select(b => b.ToString("X2")));
        var multipleFingerprints = text.Replace(
            $"a=fingerprint:sha-256 {fingerprint}",
            $"a=fingerprint:sha-384 {string.Join(':', Enumerable.Repeat("AA", 48))}\r\na=fingerprint:sha-512 {string.Join(':', Enumerable.Repeat("BB", 64))}\r\na=fingerprint:sha-256 {fingerprint}");
        Check(SdpSessionDescription.Parse(multipleFingerprints).Media.All(m => m.FingerprintSha256 == Convert.ToHexString(Fingerprint)));
        var inherited = text;
        foreach (var field in new[] { "ice-ufrag:local012", $"ice-pwd:{new string('a', 22)}", $"fingerprint:sha-256 {fingerprint}", "setup:actpass" })
            inherited = inherited.Replace($"a={field}\r\n", "");
        inherited = inherited.Replace("t=0 0\r\n", $"t=0 0\r\na=ice-ufrag:session12\r\na=ice-pwd:{new string('b', 22)}\r\na=fingerprint:sha-256 {fingerprint}\r\na=setup:actpass\r\n");
        var session = SdpSessionDescription.Parse(inherited);
        Check(session.Media.All(m => m.IceCredentials!.UsernameFragment == "session12" && m.Setup == SdpSetup.ActPass && m.FingerprintSha256 == Convert.ToHexString(Fingerprint)));
        var ipv6 = SdpSessionDescription.Parse(text.Replace("127.0.0.1", "::1"));
        Check(ipv6.Media[0].Candidates[0].GetResolvedUdpCandidate()!.EndPoint.Address.Equals(IPAddress.IPv6Loopback));
        Check(SdpSessionDescription.Parse(text.Replace("127.0.0.1", "synthetic.local")).Media[0].Candidates[0].GetResolvedUdpCandidate() == null);
    }
    internal static void Malformed()
    {
        var text = Offer();
        foreach (var key in new[] { "ice-ufrag:local012", $"ice-pwd:{new string('a', 22)}", "mid:audio", "setup:actpass", "rtcp-mux", "sctp-port:5000" })
            Reject(text.Replace($"a={key}\r\n", $"a={key}\r\na={key}\r\n"));
        Reject(text.Replace("a=group:BUNDLE audio data", "a=group:BUNDLE audio audio"));
        Reject(text.Replace("a=group:BUNDLE audio data", "a=group:BUNDLE absent data"));
        Reject(text.Replace("a=mid:data", "a=mid:audio"));
        Reject(text.Replace("opus/48000/2", "opus/0/2"));
        Reject(text.Replace("a=rtpmap:111", "a=rtpmap:112"));
        Reject(text.Replace("typ host", "typ unknown"));
        Reject(text.Replace("49152 typ", "0 typ"));
        Reject(text.Replace("2130706431", "4294967295"));
        Reject(text.Replace("a=setup:actpass", "a=setup:unexpected"));
        Check(SdpSessionDescription.Parse(text.Replace("sha-256", "sha-1")).Media.All(m => m.FingerprintSha256 == null));
        Reject(text.Replace("c=IN IP4 0.0.0.0\r\n", ""));
        Reject(text.Replace("s=-", "s=-\r\nk=clear:synthetic"));
        Reject(text.Replace("s=-", "s=-\0"));
        Reject(text.Replace("s=-", "s=-\n\n"));
        Reject(text.Replace("v=0", "v=1"));
        Reject("i=before-version\r\n" + text);
        Reject(text.Replace("a=mid:audio", "a=mid:audio\r\na=bundle-only"));
        Reject(text.Replace("a=mid:audio", "a=mid:audio\r\na=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:synthetic"));
        Reject(text.Replace("t=0 0", "t=0 1"));
        Reject(text.Replace("a=mid:audio", "a=mid:audio\r\na=extmap:1 duplicate"));
        Reject(text + string.Concat(Enumerable.Repeat("a=unknown:bounded\r\n", 129)));
        Reject(text + new string('x', 65536));
        Reject(text.Replace("s=-", "s=" + new string('ж', 32768)));
        var formats = string.Join(' ', Enumerable.Range(0, 128));
        Reject(text.Replace("UDP/TLS/RTP/SAVPF 111", $"UDP/TLS/RTP/SAVPF 111 {formats}"));
    }
    internal static void FullPayloadInventory()
    {
        // Chromium's receive-only offer legitimately lists more than 32 codec/PT alternatives.
        // RTP has a finite 7-bit payload space; retain total SDP, line and duplicate limits.
        var text = Offer();
        foreach(var count in new[] { 33, 34, 64, 128 })
        {
            var formats = new[] { 111 }.Concat(Enumerable.Range(0, 128).Where(pt => pt != 111).Take(count - 1));
            var expanded = text.Replace("UDP/TLS/RTP/SAVPF 111", $"UDP/TLS/RTP/SAVPF {string.Join(' ', formats)}");
            var parsed = SdpSessionDescription.Parse(expanded);
            Check(parsed.Media[0].Formats.Count == count && parsed.Media[0].Codecs.Single().PayloadType == 111);
            var answer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(parsed, Transport(49153), 5678));
            Check(SdpNegotiation.ValidateOpusAnswer(parsed, answer, true).AudioCodec!.PayloadType == 111);
            Reject(expanded.Replace("UDP/TLS/RTP/SAVPF", "UDP/TLS/RTP/SAVPF 111"));
        }
        foreach(var invalid in new[] { "128", "256", "-1", "VP8", "0111" })
            Reject(text.Replace("UDP/TLS/RTP/SAVPF 111", $"UDP/TLS/RTP/SAVPF 111 {invalid}"));
        var all = string.Join(' ', Enumerable.Range(0,128));
        Reject(text.Replace("UDP/TLS/RTP/SAVPF 111", $"UDP/TLS/RTP/SAVPF {all} 128"));
    }
    internal static void BrowserCodecInventory()
    {
        var offer = SdpNegotiation.CreateOffer(Transport(), 1234, 2345,
            [new() { Codec=VideoCodec.Vp8, PayloadType=96, Vp8MaximumMacroblocks=1200, Vp8MaximumFrameRate=30 }]);
        var formats = Enumerable.Range(33,32).Concat(new[] {96,97}).ToArray();
        var codecLines = string.Concat(formats.Select(pt => $"a=rtpmap:{pt} {(pt==96?"VP8":"synthetic")}/90000\r\na=fmtp:{pt} max-fs=1200;max-fr=30\r\n"));
        var feedback = string.Concat(Enumerable.Range(0,111).Select(i=>$"a=rtcp-fb:96 future{i}\r\n"));
        offer=offer.Replace("m=video 9 UDP/TLS/RTP/SAVPF 96", $"m=video 9 UDP/TLS/RTP/SAVPF {string.Join(' ',formats)}")
            .Replace("a=fmtp:96 max-fs=1200;max-fr=30\r\n", "")
            .Replace("a=rtpmap:96 VP8/90000\r\n",codecLines+feedback);
        var parsed=SdpSessionDescription.Parse(offer);
        Check(parsed.Media[1].Codecs.Count==34 && parsed.Media[1].RtcpFeedback.Count==112);
        var answer=SdpSessionDescription.Parse(SdpNegotiation.CreateAnswer(parsed,Transport(49153),5678,6789,
            [new() { Codec=VideoCodec.Vp8,PayloadType=96,Vp8MaximumMacroblocks=1200,Vp8MaximumFrameRate=30 }]));
        Check(SdpNegotiation.ValidateAnswer(parsed,answer,true).VideoFormat!.PayloadType==96 && answer.Media[1].Codecs.Count==1);
        // Preserve a smaller unknown-attribute budget even for legitimate large video inventories.
        Reject(offer.Replace("a=mid:video", "a=mid:video\r\n"+string.Concat(Enumerable.Repeat("a=unknown:bounded\r\n",129)).TrimEnd('\r','\n')));
        var video=offer.Split("m=video")[1].Split("m=application")[0];
        var count=video.Split("\r\n").Count(line=>line.StartsWith("a="));
        var padding=string.Concat(Enumerable.Repeat("a=ssrc:2345 cname:synthetic\r\n",512-count));
        Check(SdpSessionDescription.Parse(offer.Replace("a=mid:video\r\n","a=mid:video\r\n"+padding)).Media[1].Codecs.Count==34);
        Reject(offer.Replace("a=mid:video\r\n","a=mid:video\r\n"+padding+"a=ssrc:2345 cname:synthetic\r\n"));
    }
    internal static void RolesAndDirections()
    {
        foreach (var direction in Enum.GetValues<SdpDirection>())
            foreach (var setup in new[] { SdpSetup.Active, SdpSetup.Passive })
            {
                var offer = SdpSessionDescription.Parse(Offer(direction));
                var answer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(offer, Transport(49153), 5678, preferredSetup: setup));
                var local = SdpNegotiation.ValidateOpusAnswer(offer, answer, true);
                var remote = SdpNegotiation.ValidateOpusAnswer(offer, answer, false);
                Check(local.DtlsRole != remote.DtlsRole && local.IceRole == IceRole.Controlling && remote.IceRole == IceRole.Controlled);
                Check(local.CanSendAudio == (direction is SdpDirection.SendReceive or SdpDirection.SendOnly));
                Check(local.CanReceiveAudio == (direction is SdpDirection.SendReceive or SdpDirection.ReceiveOnly));
                Check(remote.CanSendAudio == local.CanReceiveAudio && remote.CanReceiveAudio == local.CanSendAudio);
                Check(local.MaximumMessageSize == 262144);
            }
        var one = SdpSessionDescription.Parse(Offer(data: false).Replace("a=group:BUNDLE audio\r\n", ""));
        var singleAnswer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(one, Transport(), 5678));
        Check(singleAnswer.BundleMids.Count == 0 && SdpNegotiation.ValidateOpusAnswer(one, singleAnswer, true).LocalData == null);
        var lite = SdpSessionDescription.Parse(Offer().Replace("t=0 0\r\n", "t=0 0\r\na=ice-lite\r\n"));
        var liteAnswer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(lite, Transport(), 5678));
        Check(SdpNegotiation.ValidateOpusAnswer(lite, liteAnswer, false).IceRole == IceRole.Controlling);
        foreach (var preferred in Enum.GetValues<SdpDirection>())
        {
            var offered = SdpSessionDescription.Parse(Offer());
            var restricted = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(offered, Transport(), 5678, direction: preferred));
            Check(restricted.Media[0].Direction == preferred);
            var local = SdpNegotiation.ValidateOpusAnswer(offered, restricted, false);
            Check(local.CanSendAudio == (preferred is SdpDirection.SendOnly or SdpDirection.SendReceive));
            Check(local.CanReceiveAudio == (preferred is SdpDirection.ReceiveOnly or SdpDirection.SendReceive));
        }
    }
    internal static void NegotiationPreflight()
    {
        var text = Offer(); var offer = SdpSessionDescription.Parse(text);
        var answer = SdpNegotiation.CreateOpusAnswer(offer, Transport(49153), 5678);
        RejectAnswer(text, answer.Replace("a=setup:active", "a=setup:actpass"));
        RejectAnswer(text, answer.Replace("111", "112"));
        RejectAnswer(text.Replace("111", "65"), answer.Replace("111", "65"));
        RejectAnswer(text, answer.Replace("opus/48000/2", "opus/48000/1"));
        RejectAnswer(text, answer.Replace("a=rtcp-mux\r\n", ""));
        RejectAnswer(text, answer.Replace("a=mid:audio", "a=mid:new"));
        RejectAnswer(text, answer.Replace("m=application", "m=video"));
        RejectAnswer(text, answer.Replace("a=extmap:1", "a=extmap:2"));
        RejectAnswer(text, answer.Replace("sctp-port:5000", "sctp-port:0"));
        RejectAnswer(text, answer.Replace("a=group:BUNDLE audio data", "a=group:BUNDLE audio"));
        var split = answer.IndexOf("m=application", StringComparison.Ordinal);
        RejectAnswer(text, answer[..split] + answer[split..].Replace(new string('a', 22), new string('b', 22)));
        var noMid = SdpSessionDescription.Parse(answer.Replace($"a=extmap:1 {SdpNegotiation.MidExtension}\r\n", ""));
        Check(SdpNegotiation.ValidateOpusAnswer(offer, noMid, true).OutgoingAudioHeaderExtensions.Count == 0);
        RejectAnswer(text.Replace("a=extmap:1 ", "a=extmap:1/recvonly "), answer);
        var limited = SdpSessionDescription.Parse(answer.Replace("a=max-message-size:262144", "a=max-message-size:1024"));
        Check(SdpNegotiation.ValidateOpusAnswer(offer, limited, true).MaximumMessageSize == 1024);
        var unlimited = SdpSessionDescription.Parse(answer.Replace("a=max-message-size:262144", "a=max-message-size:0"));
        Check(SdpNegotiation.ValidateOpusAnswer(offer, unlimited, true).MaximumMessageSize == 262144);
        // Unsupported video remains in the same position, explicitly rejected.
        var extra = text.Replace("BUNDLE audio data", "BUNDLE video audio data").Replace("m=audio", "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=mid:video\r\na=rtpmap:96 VP8/90000\r\nm=audio");
        var videoOffer = SdpSessionDescription.Parse(extra);
        var videoAnswer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(videoOffer, Transport(), 5678));
        Check(videoAnswer.Media[0].IsRejected && videoAnswer.Media[1].Mid == "audio");
        Check(SdpNegotiation.ValidateOpusAnswer(videoOffer, videoAnswer, true).AudioCodec!.Name == "opus");
    }
    internal static void RelayCandidates()
    {
        var host = new IPEndPoint(IPAddress.Loopback, 49152);
        var mapped = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 45000);
        var relay = new IceCandidate(new(IPAddress.Parse("192.0.2.20"), 46000), 16777215, IceCandidateType.Relay, mapped);
        var reflexive = new IceCandidate(new(IPAddress.Parse("192.0.2.30"), 47000), 1694498815, IceCandidateType.ServerReflexive, host);
        var transport = new SdpLocalTransport(new("local012", new string('a', 22)), Fingerprint, host,
            additionalCandidates: [relay, reflexive], gatheringComplete: false, supportsTrickle: true, relayOnly: true);
        var description = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusOffer(transport, 1234));
        Check(description.Media.All(m => m.Candidates is { Count: 1 } && m.Candidates[0].Type == IceCandidateType.Relay));
        Check(transport.Candidates[0].RelatedEndPoint!.Equals(mapped));
        Check(SdpNegotiation.CreateOpusOffer(transport, 1234).Contains("raddr 192.0.2.10 rport 45000", StringComparison.Ordinal));
        Check(!SdpNegotiation.CreateOpusOffer(transport, 1234).Contains("typ host", StringComparison.Ordinal));
        foreach (var invalid in new[] {
            new IceCandidate(mapped, 1694498815, IceCandidateType.ServerReflexive, mapped),
            new IceCandidate(new(IPAddress.IPv6Loopback, 49154), 16777215, IceCandidateType.Relay, new(IPAddress.IPv6Loopback, 49155)),
            new IceCandidate(mapped, 16777215, IceCandidateType.Relay) })
        {
            try { _ = new SdpLocalTransport(transport.Credentials, Fingerprint, host, additionalCandidates: [invalid]); throw new IOException("Invalid local relay metadata accepted"); }
            catch (ArgumentException) { }
        }
    }
    internal static void HostileCorpus()
    {
        var random = new Random(8217); var seed = Offer();
        for (var i = 0; i < 1500; i++)
        {
            var chars = seed.ToCharArray();
            for (var n = 0; n < 1 + i % 6; n++) chars[random.Next(chars.Length)] = (char)random.Next(128);
            var candidate = new string(chars);
            try { SdpSessionDescription.Parse(candidate); }
            catch (Exception error) when (error is FormatException or ArgumentException) { }
        }
    }
}
