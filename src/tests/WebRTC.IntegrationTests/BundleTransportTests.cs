using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

// Authored local two-route peer. Both routes answer ICE; bundled DTLS/media
// originate only on the negotiated tagged route. No provider SDP or packets.
internal static class BundleTransportTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new IOException(message); }
    private static async Task<T> First<T>(IAsyncEnumerable<T> items, CancellationToken ct)
    {
        await foreach (var item in items.WithCancellation(ct)) return item;
        throw new IOException("Required bundled peer data missing");
    }
    private static string CandidateOn(string sdp, string mid, IPEndPoint endpoint, uint priority)
    {
        var selected = false;
        return string.Join("\r\n", sdp.Split("\r\n").Select(line =>
        {
            if (line.StartsWith("m=", StringComparison.Ordinal)) selected = false;
            if (line == $"a=mid:{mid}") selected = true;
            return selected && line.StartsWith("a=candidate:", StringComparison.Ordinal)
                ? "a=candidate:" + new IceCandidate(endpoint, priority).ToSdpAttribute() : line;
        }));
    }
    internal static void Selection()
    {
        foreach (var dataTag in new[] { false, true })
        {
            var tagged = new IPEndPoint(IPAddress.Loopback, 49152);
            var other = new IPEndPoint(IPAddress.Loopback, 49153);
            var transport = new SdpLocalTransport(new("bundle12", new string('a', 22)), new byte[32], tagged);
            var offerText = SdpNegotiation.CreateOpusOffer(transport, 1234);
            if (dataTag) offerText = offerText.Replace("BUNDLE audio data", "BUNDLE data audio");
            offerText = CandidateOn(offerText, dataTag ? "audio" : "data", other, int.MaxValue);
            var offer = SdpSessionDescription.Parse(offerText);
            var answerText = SdpNegotiation.CreateOpusAnswer(offer, transport, 5678);
            answerText = CandidateOn(answerText, dataTag ? "audio" : "data", other, int.MaxValue);
            var answer = SdpSessionDescription.Parse(answerText);
            foreach (var offerer in new[] { false, true })
            {
                var negotiated = SdpNegotiation.ValidateOpusAnswer(offer, answer, offerer);
                Check(negotiated.RemoteCandidates.Count == 1 && negotiated.RemoteCandidates[0].GetResolvedUdpCandidate()!.EndPoint.Equals(tagged),
                    "BUNDLE tag order was replaced by media order or non-tag candidate priority");
            }
        }
        // The answer rejects a suggested video tag and selects audio. Remote
        // offer candidates must then follow that accepted tag, not the suggestion.
        var acceptedEndpoint = new IPEndPoint(IPAddress.Loopback, 49154);
        var transportWithVideo = new SdpLocalTransport(new("bundle34", new string('b', 22)), new byte[32], acceptedEndpoint);
        var withVideo = SdpNegotiation.CreateOffer(transportWithVideo, 1234, 5678,
            [new VideoCodecCapability { Codec = VideoCodec.Vp8, PayloadType = 96, Vp8MaximumMacroblocks = 3600, Vp8MaximumFrameRate = 30 }]);
        withVideo = withVideo.Replace("BUNDLE audio video data", "BUNDLE video audio data");
        withVideo = CandidateOn(withVideo, "video", new(IPAddress.Loopback, 49155), int.MaxValue);
        withVideo = CandidateOn(withVideo, "data", new(IPAddress.Loopback, 49156), int.MaxValue);
        var videoOffer = SdpSessionDescription.Parse(withVideo);
        var audioAnswer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(videoOffer, transportWithVideo, 9012));
        var selected = SdpNegotiation.ValidateOpusAnswer(videoOffer, audioAnswer, false);
        Check(selected.RemoteCandidates.Count == 1 && selected.RemoteCandidates[0].GetResolvedUdpCandidate()!.EndPoint.Equals(acceptedEndpoint),
            "Rejected suggested BUNDLE tag supplied remote transport candidates");
    }
    internal static async Task Exchange(bool expectLegacyFailure = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        var options = new PeerConnectionOptions
        {
            LocalEndPoint = new(IPAddress.Loopback, 0), ConnectionTimeout = TimeSpan.FromSeconds(4),
            Dtls = new() { HandshakeTimeout = TimeSpan.FromSeconds(2), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100) },
        };
        await using var left = new PeerConnection(options); await using var right = new PeerConnection(options);
        left.CompleteGathering(); right.CompleteGathering();
        var offer = left.CreateOffer();
        var leftEndpoint = SdpSessionDescription.Parse(offer).Media[0].Candidates[0].GetResolvedUdpCandidate()!.EndPoint;
        await using var routes = new Routes(leftEndpoint);
        // Route both directions through the fixture; direct checks must not learn a bypass.
        var routedOffer = CandidateOn(CandidateOn(offer, "audio", routes.Tagged, 1000), "data", routes.Tagged, 1000);
        var initialAnswer = right.CreateAnswer(routedOffer, SdpSetup.Passive);
        var rightEndpoint = SdpSessionDescription.Parse(initialAnswer).Media[0].Candidates[0].GetResolvedUdpCandidate()!.EndPoint;
        routes.ConfigureRight(rightEndpoint);
        var answer = CandidateOn(initialAnswer, "audio", routes.Tagged, 1000);
        answer = CandidateOn(answer, "data", routes.Decoy, 2000);
        left.SetRemoteAnswer(answer);
        using var capture = left.AttachDiagnostics(new() { Metrics = false, PacketTrace = true, EventCapacity = 512 });
        var leftConnect = left.ConnectAsync(ct); var rightConnect = right.ConnectAsync(ct);
        try { await Task.WhenAll(leftConnect, rightConnect); }
        catch (TimeoutException) when (expectLegacyFailure)
        {
            Check(routes.DecoyChecks > 0 && routes.ServerDtls > 0, "Legacy reproduction did not exercise both actual routes");
            Check(left.GetDiagnostics().Ice.SelectedRemoteEndPoint!.Equals(routes.Decoy), "Legacy selected unexpected route");
            Check(left.GetEstablishmentEvidence().Phases.Single(p => p.Phase == EstablishmentPhase.Dtls).Step == HandshakeStep.DtlsServerHello,
                "Legacy timeout did not await ServerHello");
            Check(left.GetDiagnostics().Ice.DroppedDatagrams > 0 && left.GetDiagnostics().Dtls!.RejectedRecords == 0,
                "Legacy packets did not fail before DTLS parser");
            Console.WriteLine("Published legacy package reproduced: ICE succeeds on non-tag route, server DTLS arrives on tag route and is dropped before DTLS");
            return;
        }
        Check(!expectLegacyFailure, "Legacy package unexpectedly passed two-route reproduction");
        Check(routes.DecoyChecks == 0 && left.GetDiagnostics().Ice.SelectedRemoteEndPoint!.Equals(routes.Tagged),
            "A non-tagged candidate affected the negotiated BUNDLE route");
        var channel = await left.OpenDataChannelAsync(new("bundle", "", true, DataChannelReliability.Reliable, 0, 256), ct);
        var accepted = await First(right.AcceptDataChannelsAsync(ct), ct);
        await channel.SendTextAsync("tagged-route", ct);
        Check((await First(accepted.ReceiveMessagesAsync(ct), ct)).GetText() == "tagged-route", "Bundled data failed");
        await accepted.SendTextAsync("return-route", ct);
        Check((await First(channel.ReceiveMessagesAsync(ct), ct)).GetText() == "return-route", "Reverse bundled data failed");
        var rejected = left.GetDiagnostics().Ice.DroppedDatagrams;
        await routes.InjectDecoyAsync(ct);
        while (left.GetDiagnostics().Ice.DroppedDatagrams == rejected) await Task.Delay(5, ct);
        Check(left.GetDiagnostics().Dtls!.RejectedRecords == 0 && left.State == PeerConnectionState.Connected,
            "Wrong-source DTLS bypassed ICE or killed the nominated peer");
        await right.SendOpusAsync(new byte[] { 0xf8, 0xff, 0xfe }, 960, cancellationToken: ct);
        Check((await First(left.ReceiveAudioAsync(ct), ct)).Timestamp == 960, "Permitted media failed after wrong-source rejection");
    }

    private sealed class Routes : IAsyncDisposable
    {
        private readonly Socket _tagged = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly Socket _decoy = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly IPEndPoint _left;
        private IPEndPoint? _right;
        private readonly Dictionary<string, Socket> _transactions = [];
        private readonly object _gate = new();
        private readonly Task[] _workers;
        private long _decoyChecks, _serverDtls;
        internal long DecoyChecks => Interlocked.Read(ref _decoyChecks);
        internal long ServerDtls => Interlocked.Read(ref _serverDtls);
        internal IPEndPoint Tagged => (IPEndPoint)_tagged.LocalEndPoint!;
        internal IPEndPoint Decoy => (IPEndPoint)_decoy.LocalEndPoint!;
        internal Routes(IPEndPoint left)
        {
            _left = left;
            _tagged.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _decoy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _workers = [Run(_tagged), Run(_decoy)];
        }
        internal void ConfigureRight(IPEndPoint right) => Volatile.Write(ref _right, right);
        private async Task Run(Socket socket)
        {
            var buffer = new byte[65536];
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                    var bytes = buffer.AsMemory(0, received.ReceivedBytes);
                    var right = Volatile.Read(ref _right) ?? throw new IOException("Fixture remote missing");
                    if (received.RemoteEndPoint.Equals(_left))
                    {
                        if (StunMessage.TryParse(bytes.Span, out var request) && request.Type == StunMessage.BindingRequest)
                        {
                            lock (_gate)
                            {
                                if (_transactions.Count >= 128) throw new IOException("Fixture transaction bound exceeded");
                                _transactions[Convert.ToHexString(request.TransactionId)] = socket;
                            }
                            if (socket == _decoy) Interlocked.Increment(ref _decoyChecks);
                        }
                        await _tagged.SendToAsync(bytes, SocketFlags.None, right, _lifetime.Token);
                    }
                    else if (socket == _tagged && received.RemoteEndPoint.Equals(right))
                    {
                        var route = _tagged;
                        if (StunMessage.TryParse(bytes.Span, out var response) && response.Type is 0x0101 or 0x0111)
                            lock (_gate) if (_transactions.Remove(Convert.ToHexString(response.TransactionId), out var original)) route = original;
                        if (bytes.Length != 0 && bytes.Span[0] is >= 20 and <= 63) Interlocked.Increment(ref _serverDtls);
                        await route.SendToAsync(bytes, SocketFlags.None, _left, _lifetime.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        internal async Task InjectDecoyAsync(CancellationToken ct) =>
            await _decoy.SendToAsync(new byte[] { 22, 0xfe, 0xfd }, SocketFlags.None, _left, ct);
        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel(); await Task.WhenAll(_workers); _tagged.Dispose(); _decoy.Dispose(); _lifetime.Dispose();
        }
    }
}
