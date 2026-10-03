using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class PeerTests
{
    private static void Check(bool value, string text = "Owned peer assertion failed") => SctpTests.Check(value, text);
    private static PeerConnectionOptions Options(bool data = true, int queue = 64) => new()
    { LocalEndPoint = new(IPAddress.Loopback, 0), DataChannels = data, AudioQueueCapacity = queue, ConnectionTimeout = TimeSpan.FromSeconds(5) };
    private static DataChannelParameters Channel() => new("oai-events", "", true, DataChannelReliability.Reliable, 0, 256);
    private static async Task<T> First<T>(IAsyncEnumerable<T> items, CancellationToken ct)
    { await foreach (var item in items.WithCancellation(ct)) return item; throw new IOException("Peer stream ended unexpectedly"); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); throw new IOException("Unsafe peer operation accepted"); } catch (T) { } }
    private static async Task Connect(PeerConnection left, PeerConnection right, CancellationToken ct, SdpSetup setup = SdpSetup.Active)
    {
        var offer = left.CreateOffer(); var answer = right.CreateAnswer(offer, setup); left.SetRemoteAnswer(answer);
        await Task.WhenAll(left.ConnectAsync(ct), right.ConnectAsync(ct));
    }
    internal static async Task Local(SdpSetup setup)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var left = new PeerConnection(Options()); await using var right = new PeerConnection(Options());
        await Connect(left, right, ct, setup);
        Check(left.State == PeerConnectionState.Connected && left.MediaReady.IsCompletedSuccessfully && left.DataChannelsReady.IsCompletedSuccessfully);
        Check(left.GetDiagnostics().MediaReadyTime <= left.GetDiagnostics().ConnectedTime);
        var channel = await left.OpenDataChannelAsync(Channel(), ct); var accepted = await First(right.AcceptDataChannelsAsync(ct), ct);
        await channel.SendTextAsync("owned peer", ct); Check((await First(accepted.ReceiveMessagesAsync(ct), ct)).GetText() == "owned peer");
        var payload = SdpTests.OpusPayload();
        await left.SendOpusAsync(payload, uint.MaxValue - 959, true, ct);
        await left.SendOpusAsync(payload, 0, false, ct);
        var first = await First(right.ReceiveAudioAsync(ct), ct); var second = await First(right.ReceiveAudioAsync(ct), ct);
        Check(first.Payload.SequenceEqual(payload) && first.Marker && first.SynchronizationSource == left.AudioSource && first.Timestamp == uint.MaxValue - 959);
        Check(second.SequenceNumber == unchecked((ushort)(first.SequenceNumber + 1)) && second.Timestamp == 0 && !second.Marker);
        await right.SendOpusAsync(payload, 96000, cancellationToken: ct);
        Check((await First(left.ReceiveAudioAsync(ct), ct)).SynchronizationSource == right.AudioSource);
        var rr = Convert.FromHexString("80C9000100000000"); BinaryPrimitives.WriteUInt32BigEndian(rr.AsSpan(4), left.AudioSource);
        await left.SendRtcpAsync(rr, ct); Check((await First(right.ReceiveRtcpAsync(ct), ct)).SequenceEqual(rr));
        await Reject<ArgumentOutOfRangeException>(() => left.SendOpusAsync(new byte[left.MaximumAudioPayloadBytes + 1], 960, cancellationToken: ct).AsTask());
        await channel.CloseAsync(ct); await left.CloseAsync(ct);
        Check(await right.Completion.WaitAsync(ct) == null && right.State == PeerConnectionState.Closed);
        await Reject<ObjectDisposedException>(() => left.SendOpusAsync(payload, 1).AsTask());
    }
    internal static async Task Signaling()
    {
        await using var left = new PeerConnection(Options() with { Channels = new() { ReceiveBufferBytes = 16384 } }); await using var right = new PeerConnection(Options());
        await Reject<InvalidOperationException>(() => left.ConnectAsync());
        await Reject<InvalidOperationException>(() => left.SendOpusAsync(SdpTests.OpusPayload(), 0).AsTask());
        var offer = left.CreateOffer();
        Check(SdpSessionDescription.Parse(offer).Media[1].MaximumMessageSize == 16384);
        await Reject<FormatException>(() => Task.FromResult(right.CreateAnswer("malformed")));
        Check(right.State == PeerConnectionState.New);
        var answer = right.CreateAnswer(offer);
        await Reject<InvalidOperationException>(() => Task.Run(() => left.SetRemoteAnswer(answer.Replace("setup:active", "setup:actpass"))));
        Check(left.State == PeerConnectionState.HaveLocalOffer);
        left.SetRemoteAnswer(answer);
        await Reject<InvalidOperationException>(() => Task.FromResult(left.CreateOffer()));
        await Reject<InvalidOperationException>(() => Task.Run(() => left.AddRemoteCandidate("1 1 UDP 1 127.0.0.1 12345 typ host")));
        Check(!left.ToString().Contains("ice-pwd"));
        await Task.WhenAll(left.DisposeAsync().AsTask(), left.DisposeAsync().AsTask()); Check(left.State == PeerConnectionState.Closed && await left.Completion == null);
        await Reject<ObjectDisposedException>(() => Task.FromResult(left.CreateOffer()));
    }
    internal static async Task Cancellation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var left = new PeerConnection(Options()); await using var right = new PeerConnection(Options());
        var answer = right.CreateAnswer(left.CreateOffer()); left.SetRemoteAnswer(answer);
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Reject<OperationCanceledException>(() => left.ConnectAsync(canceled.Token));
        Check(await left.Completion.WaitAsync(deadline.Token) is OperationCanceledException && left.State == PeerConnectionState.Failed);
        var endpoint = SdpSessionDescription.Parse(left.LocalDescription!).Media[0].Candidates[0].GetResolvedUdpCandidate()!.EndPoint;
        using var rebound = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); rebound.Bind(endpoint);
        await using var pending = new PeerConnection(Options()); await using var remote = new PeerConnection(Options());
        pending.SetRemoteAnswer(remote.CreateAnswer(pending.CreateOffer()));
        var connection = pending.ConnectAsync(deadline.Token); await pending.DisposeAsync();
        await Reject<ObjectDisposedException>(() => connection); Check(await pending.Completion == null);
    }
    internal static async Task Direction()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var left = new PeerConnection(Options(false) with { AudioDirection = SdpDirection.SendOnly });
        await using var right = new PeerConnection(Options(false) with { AudioDirection = SdpDirection.ReceiveOnly });
        await Connect(left, right, ct);
        await left.SendOpusAsync(SdpTests.OpusPayload(), 1234, cancellationToken: ct);
        Check((await First(right.ReceiveAudioAsync(ct), ct)).Timestamp == 1234);
        await Reject<InvalidOperationException>(() => right.SendOpusAsync(SdpTests.OpusPayload(), 1234).AsTask());
        await Reject<NotSupportedException>(() => left.OpenDataChannelAsync(Channel(), ct));
        await Reject<NotSupportedException>(() => left.DataChannelsReady);
    }
    internal static async Task Queue()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var left = new PeerConnection(Options(false)); await using var right = new PeerConnection(Options(false, 1));
        await Connect(left, right, ct);
        for (uint n = 0; n < 8; n++) await left.SendOpusAsync(SdpTests.OpusPayload(), 960 * n, cancellationToken: ct);
        while (right.GetDiagnostics().DroppedAudioPackets < 7) await Task.Delay(5, ct);
        Check((await First(right.ReceiveAudioAsync(ct), ct)).Timestamp == 6720 && right.GetDiagnostics().DroppedAudioPackets == 7);
    }
    internal static async Task Routing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7)); var ct = deadline.Token;
        await using var peer = new PeerConnection(Options(false) with { MaximumAudioSources = 1 });
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var offerText = SdpNegotiation.CreateOpusOffer(new(ice.LocalCredentials, identity.GetFingerprintSha256(), ice.LocalEndPoint), 1234, false);
        offerText = offerText.Replace("BUNDLE audio", "BUNDLE video audio").Replace("m=audio", "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=mid:video\r\na=rtpmap:96 VP8/90000\r\na=ssrc:4444 cname:synthetic\r\nm=audio");
        var offer = SdpSessionDescription.Parse(offerText); var answer = SdpSessionDescription.Parse(peer.CreateAnswer(offerText));
        var session = SdpNegotiation.ValidateOpusAnswer(offer, answer, true); var connection = peer.ConnectAsync(ct);
        await ice.ConnectAsync(session.RemoteCredentials, session.IceRole, session.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), ct);
        await using var dtls = new DtlsSrtpTransport(ice, identity, session.DtlsRole, Convert.FromHexString(session.RemoteFingerprintSha256));
        await Task.WhenAll(connection, dtls.ConnectAsync(ct));
        var sequence = (ushort)0;
        byte[] Packet(uint source = 1234)
        { var packet = SdpTests.AudioPacket(session, source); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++); return packet; }
        var wrongType = Packet(); wrongType[1] = 112; await dtls.SendRtpAsync(wrongType, ct);
        var wrongMid = Packet(); "other"u8.CopyTo(wrongMid.AsSpan(17)); await dtls.SendRtpAsync(wrongMid, ct);
        var malformedMid = Packet(); malformedMid[16] = 0x1F; await dtls.SendRtpAsync(malformedMid, ct);
        var original = Packet(); var duplicate = new byte[original.Length + 8]; original.AsSpan(0, 16).CopyTo(duplicate);
        BinaryPrimitives.WriteUInt16BigEndian(duplicate.AsSpan(14), 4);
        duplicate[16] = duplicate[22] = 0x14; "audio"u8.CopyTo(duplicate.AsSpan(17)); "other"u8.CopyTo(duplicate.AsSpan(23));
        original.AsSpan(24).CopyTo(duplicate.AsSpan(32)); await dtls.SendRtpAsync(duplicate, ct);
        await dtls.SendRtpAsync(Packet(4444), ct); await dtls.SendRtpAsync(Packet(peer.AudioSource), ct);
        await dtls.SendRtpAsync(Packet(), ct); await dtls.SendRtpAsync(Packet(5678), ct);
        await dtls.SendRtcpAsync(Convert.FromHexString("81C90001000004D2"), ct);
        await dtls.SendRtcpAsync(Convert.FromHexString("80C90001000004D2"), ct);
        Check((await First(peer.ReceiveAudioAsync(ct), ct)).SynchronizationSource == 1234);
        Check((await First(peer.ReceiveRtcpAsync(ct), ct))[0] == 0x80);
        Check(peer.GetDiagnostics() is { RejectedAudioPackets: 7, RejectedControlPackets: 1 });
    }
    internal static async Task EarlyMedia(bool extendedMid = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7)); var ct = deadline.Token;
        await using var peer = new PeerConnection(Options());
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var offerText = SdpNegotiation.CreateOpusOffer(new(ice.LocalCredentials, identity.GetFingerprintSha256(), ice.LocalEndPoint), 1234);
        const string longMid = "synthetic-audio-mid-long";
        if (extendedMid) offerText = offerText.Replace("BUNDLE audio data", $"BUNDLE {longMid} data").Replace("mid:audio", $"mid:{longMid}").Replace("extmap:1 ", "extmap:200 ");
        var session = SdpNegotiation.ValidateOpusAnswer(SdpSessionDescription.Parse(offerText), SdpSessionDescription.Parse(peer.CreateAnswer(offerText)), true);
        var connecting = peer.ConnectAsync(ct);
        await ice.ConnectAsync(session.RemoteCredentials, session.IceRole, session.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), ct);
        await using var dtls = new DtlsSrtpTransport(ice, identity, session.DtlsRole, Convert.FromHexString(session.RemoteFingerprintSha256));
        await dtls.ConnectAsync(ct); await peer.MediaReady.WaitAsync(ct);
        // Deliberately never start the other SCTP endpoint. Media must not wait for it.
        Check(peer.State == PeerConnectionState.Connecting && !connecting.IsCompleted && !peer.DataChannelsReady.IsCompleted);
        await peer.SendOpusAsync(SdpTests.OpusPayload(), 96000, cancellationToken: ct);
        var datagram = await First(dtls.ReceiveMediaDatagramsAsync(ct), ct);
        Check(datagram.Kind == SecureMediaKind.Rtp && RtpPacket.TryParse(datagram.Data, out var packet) && packet.Timestamp == 96000);
        if (extendedMid)
        {
            Check(RtpPacket.TryParse(datagram.Data, out var extended) && extended.ExtensionProfile == 0x1000);
            Check(RtpHeaderExtensions.TryRead(extended, 200, out var mid) && System.Text.Encoding.ASCII.GetString(mid) == longMid);
        }
        await dtls.SendRtpAsync(SdpTests.AudioPacket(session, 1234), ct);
        Check((await First(peer.ReceiveAudioAsync(ct), ct)).Timestamp == 96000);
        await peer.DisposeAsync(); await Reject<ObjectDisposedException>(() => connecting);
    }
    internal static async Task CandidatePolicy()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        var checks = 0;
        await using var left = new PeerConnection(Options(false) with { CandidateFilter = _ => { Interlocked.Increment(ref checks); return false; } });
        await using var right = new PeerConnection(Options(false));
        var offer = left.CreateOffer(); var answer = right.CreateAnswer(offer); left.SetRemoteAnswer(answer);
        var local = left.ConnectAsync(ct); var remote = right.ConnectAsync(ct);
        await Task.Delay(250, ct);
        Check(!local.IsCompleted && !remote.IsCompleted && !left.MediaReady.IsCompleted,
            "Peer-reflexive learning bypassed the application destination policy");
        Check(left.GetDiagnostics().Ice is { CandidatePairs: 0, SentChecks: 0, ValidatedRequests: 0 } && checks > 1);
        deadline.Cancel();
        await Reject<OperationCanceledException>(() => local); await Reject<OperationCanceledException>(() => remote);
    }
    internal static async Task Pion(Uri uri, bool offerer, bool passive, bool relay = false, bool trickleRelay = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9)); var ct = deadline.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await using var peer = new PeerConnection(Options() with { Sctp = SctpTests.Fast(),
            CandidateFilter = relay ? c => c.Type == IceCandidateType.Relay : null }); string? id = null;
        try
        {
            var request = new SessionRequest(offerer ? peer.CreateOffer() : "", passive, relay);
            using var response = await http.PostAsJsonAsync(new Uri(uri, offerer ? "/session/answer" : "/session/offer"), request, InteropJson.Default.SessionRequest, ct);
            response.EnsureSuccessStatusCode(); var description = await response.Content.ReadFromJsonAsync(InteropJson.Default.SessionResponse, ct) ?? throw new IOException("Independent peer missing");
            id = description.Id; Check(id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit));
            if (relay)
            {
                Check(description.StunPort is > 0 and <= 65535);
                var baseCandidate = peer.GetLocalCandidates()[0];
                var mapped = await peer.GatherServerReflexiveCandidateAsync(new(IPAddress.Loopback, description.StunPort), GatheringTests.Fast(), ct);
                Check(mapped.EndPoint.Equals(baseCandidate.EndPoint) && mapped.RelatedEndPoint!.Equals(baseCandidate.EndPoint));
                Check(peer.GetLocalCandidates().Count == 1 && peer.GetGatheringDiagnostics().SuccessfulBindings == 1, "Independent STUN mapped a different socket or retained a redundant host mapping");
                peer.CompleteGathering();
            }
            var remoteCandidates = SdpSessionDescription.Parse(description.Sdp).Media.SelectMany(m => m.Candidates).ToArray();
            if (relay) Check(remoteCandidates.Length > 0 && remoteCandidates.All(c => c.Type == IceCandidateType.Relay), "Independent relay-only peer advertised a direct fallback");
            var remoteSdp = trickleRelay ? string.Join("\r\n", description.Sdp.Split("\r\n").Where(l => !l.StartsWith("a=candidate:", StringComparison.Ordinal) && l != "a=end-of-candidates")) : description.Sdp;
            if (offerer) peer.SetRemoteAnswer(remoteSdp);
            else
            {
                var answer = peer.CreateAnswer(remoteSdp, passive ? SdpSetup.Passive : SdpSetup.Active);
                using var accepted = await http.PostAsJsonAsync(new Uri(uri, $"/session/{id}/answer"), new SessionRequest(answer, false), InteropJson.Default.SessionRequest, ct);
                accepted.EnsureSuccessStatusCode();
            }
            var connection = peer.ConnectAsync(ct);
            if (trickleRelay) foreach (var candidate in remoteCandidates.DistinctBy(c => $"{c.Address}:{c.Port}"))
                peer.AddRemoteCandidate(candidate.GetResolvedUdpCandidate()!.ToSdpAttribute());
            await connection;
            if (relay) Check(peer.GetDiagnostics().Ice.SelectedRemoteCandidateType == IceCandidateType.Relay &&
                remoteCandidates.Any(c => c.GetResolvedUdpCandidate()!.EndPoint.Equals(peer.GetDiagnostics().Ice.SelectedRemoteEndPoint)), "Relay was not the selected destination");
            var channel = offerer ? await peer.OpenDataChannelAsync(Channel(), ct) : await First(peer.AcceptDataChannelsAsync(ct), ct);
            if (!offerer) Check((await First(channel.ReceiveMessagesAsync(ct), ct)).GetText() == "pion:ready");
            await channel.SendTextAsync("owned controls", ct);
            await peer.SendOpusAsync(SdpTests.OpusPayload(), 96000, true, ct); await peer.SendOpusAsync(SdpTests.OpusPayload(), 96960, false, ct);
            var audio = await First(peer.ReceiveAudioAsync(ct), ct); var next = await First(peer.ReceiveAudioAsync(ct), ct);
            Check(audio.Timestamp == 96000 && audio.Marker && next.Timestamp == 96960 && !next.Marker && next.SequenceNumber == unchecked((ushort)(audio.SequenceNumber + 1)));
            Check(audio.Payload.SequenceEqual(SdpTests.OpusPayload()) && next.Payload.SequenceEqual(audio.Payload));
            Check((await First(channel.ReceiveMessagesAsync(ct), ct)).GetText() == "owned controls");
            var stats = await http.GetFromJsonAsync(new Uri(uri, $"/session/{id}/stats"), InteropJson.Default.SessionStats, ct);
            Check(stats is { Audio: 2, Data: 1, Failures: 0 } && (!relay || stats.RelayAllocations > 0)); await channel.CloseAsync(ct); await peer.CloseAsync(ct);
        }
        finally
        {
            if (id != null) { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var deleted = await http.DeleteAsync(new Uri(uri, $"/session/{id}"), cleanup.Token); deleted.EnsureSuccessStatusCode(); }
        }
    }
}
