using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using tryAGI.WebRTC;

internal static class SdpTests
{
    // Original 440 Hz synthetic sine, 48k mono, libopus via FFmpeg 8.0; provenance in docs/provenance.md.
    internal static byte[] OpusPayload() => Convert.FromHexString("7881A8B036089FC201D66EF7DFFADA025AF3F4969ED2892A0995E48742F90670483DAD77C7F0A9A749175731FD11D709FF8D7B1B5F70A9480AA33804");
    private static void Check(bool value, string message = "SDP session assertion failed") => SctpTests.Check(value, message);
    internal static byte[] AudioPacket(SdpNegotiatedSession session, uint source, uint timestamp = 96000)
    {
        var audio = session.LocalAudio!;
        var extension = session.OutgoingAudioHeaderExtensions.FirstOrDefault(e => e.Value == SdpNegotiation.MidExtension);
        var mid = System.Text.Encoding.ASCII.GetBytes(audio.Mid);
        var extensionBytes = extension.Key is > 0 and < 15 && mid.Length <= 16 ? (1 + mid.Length + 3) & ~3 : 0;
        var header = 12 + (extensionBytes == 0 ? 0 : 4 + extensionBytes);
        var payload = OpusPayload(); var packet = new byte[header + payload.Length];
        packet[0] = extensionBytes == 0 ? (byte)0x80 : (byte)0x90; packet[1] = session.AudioCodec!.PayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 42); BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), source);
        if (extensionBytes != 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12), 0xBEDE);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), (ushort)(extensionBytes / 4));
            packet[16] = (byte)((extension.Key << 4) | (mid.Length - 1)); mid.CopyTo(packet, 17);
        }
        payload.CopyTo(packet, header); return packet;
    }
    private static async Task CheckAudio(DtlsSrtpTransport transport, SdpNegotiatedSession session, CancellationToken ct)
    {
        await foreach (var datagram in transport.ReceiveMediaDatagramsAsync(ct))
        {
            if (datagram.Kind == SecureMediaKind.Rtcp) continue;
            Check(RtpPacket.TryParse(datagram.Data, out var packet) && packet.PayloadType == session.AudioCodec!.PayloadType &&
                packet.Timestamp == 96000 && packet.Payload.SequenceEqual(OpusPayload()), "Negotiated encoded Opus payload or source clock changed");
            return;
        }
        throw new IOException("Negotiated media ended before audio");
    }
    private static IceCandidate[] Candidates(SdpNegotiatedSession session) =>
        session.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()).OfType<IceCandidate>().DistinctBy(c => c.EndPoint.ToString()).ToArray();

    internal static async Task Local()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var leftIce = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var rightIce = new IceUdpTransport(new(IPAddress.Loopback, 0));
        using var leftIdentity = DtlsIdentity.Generate(); using var rightIdentity = DtlsIdentity.Generate();
        var offer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusOffer(new(leftIce.LocalCredentials, leftIdentity.GetFingerprintSha256(), leftIce.LocalEndPoint), 1234));
        var answer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(offer, new(rightIce.LocalCredentials, rightIdentity.GetFingerprintSha256(), rightIce.LocalEndPoint), 5678));
        var leftSession = SdpNegotiation.ValidateOpusAnswer(offer, answer, true); var rightSession = SdpNegotiation.ValidateOpusAnswer(offer, answer, false);
        Check(leftSession.DtlsRole == DtlsRole.Server && rightSession.DtlsRole == DtlsRole.Client);
        await Task.WhenAll(leftIce.ConnectAsync(leftSession.RemoteCredentials, leftSession.IceRole, Candidates(leftSession), ct),
            rightIce.ConnectAsync(rightSession.RemoteCredentials, rightSession.IceRole, Candidates(rightSession), ct));
        await using var leftDtls = new DtlsSrtpTransport(leftIce, leftIdentity, leftSession.DtlsRole, Convert.FromHexString(leftSession.RemoteFingerprintSha256));
        await using var rightDtls = new DtlsSrtpTransport(rightIce, rightIdentity, rightSession.DtlsRole, Convert.FromHexString(rightSession.RemoteFingerprintSha256));
        await Task.WhenAll(leftDtls.ConnectAsync(ct), rightDtls.ConnectAsync(ct));
        await using var leftSctp = new SctpAssociation(leftDtls, SctpRole.Responder);
        await using var rightSctp = new SctpAssociation(rightDtls, SctpRole.Initiator);
        await Task.WhenAll(leftSctp.ConnectAsync(ct), rightSctp.ConnectAsync(ct));
        await using var leftChannels = new DataChannelAssociation(leftSctp); await using var rightChannels = new DataChannelAssociation(rightSctp);
        var channel = await leftChannels.OpenChannelAsync("oai-events", cancellationToken: ct); var accepted = await DataChannelTests.Accept(rightChannels, ct);
        await channel.SendTextAsync("SDP controls", ct);
        await leftDtls.SendRtpAsync(AudioPacket(leftSession, 1234), ct); await rightDtls.SendRtpAsync(AudioPacket(rightSession, 5678), ct);
        await Task.WhenAll(CheckAudio(rightDtls, rightSession, ct), CheckAudio(leftDtls, leftSession, ct));
        Check((await DataChannelTests.Read(accepted, ct)).GetText() == "SDP controls");
        await channel.CloseAsync(ct); await leftSctp.CloseAsync(ct);
    }
    internal static async Task Pion(Uri uri, bool localOfferer, bool passiveAnswer)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9)); var ct = deadline.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var localTransport = new SdpLocalTransport(ice.LocalCredentials, identity.GetFingerprintSha256(), ice.LocalEndPoint, maximumMessageSize: 16384);
        string? id = null;
        try
        {
            var localText = localOfferer ? SdpNegotiation.CreateOpusOffer(localTransport, 1234) : "";
            using var response = await http.PostAsJsonAsync(new Uri(uri, localOfferer ? "/session/answer" : "/session/offer"),
                new SessionRequest(localText, passiveAnswer), InteropJson.Default.SessionRequest, ct);
            response.EnsureSuccessStatusCode();
            var remote = await response.Content.ReadFromJsonAsync(InteropJson.Default.SessionResponse, ct) ?? throw new IOException("Independent SDP missing");
            id = remote.Id; Check(id.All(char.IsAsciiDigit) && id.Length is > 0 and <= 20);
            var remoteDescription = SdpSessionDescription.Parse(remote.Sdp);
            if (!localOfferer)
            {
                localText = SdpNegotiation.CreateOpusAnswer(remoteDescription, localTransport, 1234,
                    preferredSetup: passiveAnswer ? SdpSetup.Passive : SdpSetup.Active);
                using var accepted = await http.PostAsJsonAsync(new Uri(uri, $"/session/{id}/answer"),
                    new SessionRequest(localText, false), InteropJson.Default.SessionRequest, ct); accepted.EnsureSuccessStatusCode();
            }
            var local = SdpSessionDescription.Parse(localText);
            var session = localOfferer ? SdpNegotiation.ValidateOpusAnswer(local, remoteDescription, true) :
                SdpNegotiation.ValidateOpusAnswer(remoteDescription, local, false);
            Check(session.DtlsRole == (localOfferer ? passiveAnswer ? DtlsRole.Client : DtlsRole.Server : passiveAnswer ? DtlsRole.Server : DtlsRole.Client));
            Check(session.CanSendAudio && session.CanReceiveAudio && session.MaximumMessageSize == 16384);
            var candidates = Candidates(session); Check(candidates.Length > 0 && candidates.All(c => IPAddress.IsLoopback(c.EndPoint.Address)));
            await ice.ConnectAsync(session.RemoteCredentials, session.IceRole, candidates, ct);
            await using var dtls = new DtlsSrtpTransport(ice, identity, session.DtlsRole, Convert.FromHexString(session.RemoteFingerprintSha256));
            await dtls.ConnectAsync(ct);
            await using var sctp = new SctpAssociation(dtls, session.DtlsRole == DtlsRole.Client ? SctpRole.Initiator : SctpRole.Responder,
                SctpTests.Fast() with { LocalPort = session.LocalData!.SctpPort!.Value, RemotePort = session.RemoteData!.SctpPort!.Value,
                    MaximumMessageSize = session.MaximumMessageSize, ReceiveBufferBytes = session.MaximumMessageSize });
            await sctp.ConnectAsync(ct); await using var channels = new DataChannelAssociation(sctp);
            var channel = localOfferer ? await channels.OpenChannelAsync("oai-events", cancellationToken: ct) : await DataChannelTests.Accept(channels, ct);
            if (!localOfferer) Check((await DataChannelTests.Read(channel, ct)).GetText() == "pion:ready");
            await channel.SendTextAsync("SDP controls", ct);
            await dtls.SendRtpAsync(AudioPacket(session, 1234), ct);
            await CheckAudio(dtls, session, ct);
            Check((await DataChannelTests.Read(channel, ct)).GetText() == "SDP controls");
            var stats = await http.GetFromJsonAsync(new Uri(uri, $"/session/{id}/stats"), InteropJson.Default.SessionStats, ct);
            Check(stats is { Audio: 1, Data: 1, Failures: 0 }, "Independent full peer did not map encoded audio/control correctly");
            await channel.CloseAsync(ct);
        }
        finally
        {
            if (id != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = await http.DeleteAsync(new Uri(uri, $"/session/{id}"), cleanup.Token);
                response.EnsureSuccessStatusCode();
            }
        }
    }
}
internal sealed record SessionRequest(string Sdp, bool Passive, bool Relay = false, string Video = "");
internal sealed record SessionResponse(string Id, string Sdp, int StunPort = 0);
internal sealed record SessionStats(int Audio, int Data, int Failures, int RelayAllocations = 0, int Video = 0);
