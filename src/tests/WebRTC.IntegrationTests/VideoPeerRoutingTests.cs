using System.Buffers.Binary;
using System.Net;
using System.Text;
using tryAGI.WebRTC;

internal static partial class VideoPeerTests
{
    internal static async Task RoutingAndBudget()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var peer = new PeerConnection(Options(VideoCodec.Vp8, SrtpProfile.AeadAes128Gcm) with
        { Video = new() { MaximumSources = 2, MaximumFrameBytes = 64, MaximumBufferedBytes = 128, QueueCapacity = 1, MaximumFrameAge = TimeSpan.FromSeconds(1) } });
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var offer = SdpNegotiation.CreateOffer(new(ice.LocalCredentials, identity.GetFingerprintSha256(), ice.LocalEndPoint), 1234, 4444, [SdpVideoTests.Vp8()], false,
            videoDirection: SdpDirection.SendOnly);
        // Exercise authenticated dynamic video sources when SDP does not announce an SSRC.
        offer = string.Join("\r\n", offer.Split("\r\n").Where(l => !l.StartsWith("a=ssrc:4444", StringComparison.Ordinal)));
        var answer = peer.CreateAnswer(offer);
        var session = SdpNegotiation.ValidateAnswer(SdpSessionDescription.Parse(offer), SdpSessionDescription.Parse(answer), true);
        var connect = peer.ConnectAsync(ct);
        await ice.ConnectAsync(session.RemoteCredentials, session.IceRole, session.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), ct);
        await using var dtls = new DtlsSrtpTransport(ice, identity, session.DtlsRole, Convert.FromHexString(session.RemoteFingerprintSha256), new() { Profiles = [SrtpProfile.AeadAes128Gcm] });
        await Task.WhenAll(connect, dtls.ConnectAsync(ct));
        var sequences = new Dictionary<uint, ushort>();
        byte[] Packet(uint source, byte[] payload, bool marker = false, string mid = "video", byte type = 96)
        {
            var p = new byte[24 + payload.Length]; p[0] = 0x90; p[1] = (byte)(type | (marker ? 128 : 0));
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), sequences.GetValueOrDefault(source)); sequences[source] = (ushort)(sequences.GetValueOrDefault(source) + 1);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), 9000); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), source);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(12), 0xBEDE); BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(14), 2); p[16] = 0x14;
            Encoding.ASCII.GetBytes(mid).CopyTo(p, 17); payload.CopyTo(p, 24); return p;
        }
        var fragment = new byte[61]; fragment[0] = 0x10; fragment[1] = 1;
        await dtls.SendRtpAsync(Packet(100, fragment, mid: "other"), ct);
        await dtls.SendRtpAsync(Packet(1234, fragment), ct); // Announced audio source is excluded from video.
        await dtls.SendRtpAsync(Packet(peer.VideoSource, fragment), ct); // Local source cannot be reflected as remote.
        await dtls.SendRtpAsync(Packet(100, new byte[] { 0x80 }), ct); // Malformed descriptor must not consume a source slot.
        await dtls.SendRtpAsync(Packet(100, fragment), ct); await dtls.SendRtpAsync(Packet(101, fragment), ct);
        await dtls.SendRtpAsync(Packet(102, fragment), ct); // Global byte budget and source count forbid this allocation.
        while (peer.GetVideoDiagnostics().RejectedPackets < 5) await Task.Delay(5, ct);
        Check(peer.GetVideoDiagnostics() is { Sources: 2, BufferedBytes: 122, BufferedFrames: 2 });
        // Changing the PT/MID of an already learned video SSRC must not reclassify it as audio.
        await dtls.SendRtpAsync(Packet(100, new byte[] { 0, 2, 3, 4 }, true), ct);
        var complete = await First(peer.ReceiveVideoAsync(ct), ct); Check(complete.Payload.Length == 63 && complete.SynchronizationSource == 100);
        await dtls.SendRtpAsync(Packet(100, new byte[] { 0xf8, 0xff, 0xfe }, true, "audio", 111), ct);
        while (peer.GetDiagnostics().RejectedAudioPackets == 0) await Task.Delay(5, ct);
        Check(peer.GetDiagnostics().RejectedAudioPackets == 1);
        // One complete frame released its partial storage, but lifetime source ownership stays bounded.
        await dtls.SendRtpAsync(Packet(102, new byte[] { 0x10, 1, 2, 3 }, true), ct);
        while (peer.GetVideoDiagnostics().RejectedPackets < 6) await Task.Delay(5, ct);
        Check(peer.GetVideoDiagnostics().Sources == 2);
        while (peer.GetVideoDiagnostics().BufferedBytes != 0) await Task.Delay(5, ct);
        Check(peer.GetVideoDiagnostics().DroppedFrames > 0);
    }
}
