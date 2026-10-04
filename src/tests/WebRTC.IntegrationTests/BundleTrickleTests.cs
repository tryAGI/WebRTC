using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

// Authored signaling fixtures. No provider SDP, credentials, or captured media.
internal static class BundleTrickleTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new IOException($"Expected {typeof(T).Name}");
    }

    private static async Task<T> First<T>(IAsyncEnumerable<T> source, CancellationToken ct)
    {
        await foreach (var item in source.WithCancellation(ct)) return item;
        throw new IOException("Required encrypted peer data missing");
    }

    private static IPEndPoint EndPoint(string sdp) =>
        SdpSessionDescription.Parse(sdp).Media[0].Candidates[0].GetResolvedUdpCandidate()!.EndPoint;

    private static string WithoutTaggedCandidates(string sdp, bool dataTag, IPEndPoint? decoy)
    {
        var tagged = false;
        var lines = new List<string>();
        foreach (var original in sdp.Split("\r\n"))
        {
            var line = original;
            if (line.StartsWith("m=", StringComparison.Ordinal)) tagged = false;
            if (line.StartsWith("a=mid:", StringComparison.Ordinal)) tagged = line == (dataTag ? "a=mid:data" : "a=mid:audio");
            if (line == "a=end-of-candidates") continue;
            if (line.StartsWith("a=candidate:", StringComparison.Ordinal))
            {
                if (tagged || decoy == null) continue;
                line = "a=candidate:" + new IceCandidate(decoy, int.MaxValue).ToSdpAttribute();
            }
            lines.Add(line);
        }
        return string.Join("\r\n", lines);
    }

    internal static async Task Exchange(SdpSetup setup, bool dataTag, bool decoy, bool beforeConnect)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var options = new PeerConnectionOptions
        {
            LocalEndPoint = new(IPAddress.Loopback, 0),
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };
        await using var left = new PeerConnection(options);
        await using var right = new PeerConnection(options);
        using var unusedRoute = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        unusedRoute.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var unusedEndPoint = (IPEndPoint)unusedRoute.LocalEndPoint!;
        left.CompleteGathering(); right.CompleteGathering();
        var offer = left.CreateOffer();
        if (dataTag) offer = offer.Replace("BUNDLE audio data", "BUNDLE data audio", StringComparison.Ordinal);
        var leftEndPoint = EndPoint(offer);
        var signaledOffer = WithoutTaggedCandidates(offer, dataTag, decoy ? unusedEndPoint : null);
        var answer = right.CreateAnswer(signaledOffer, setup);
        var rightEndPoint = EndPoint(answer);
        var signaledAnswer = WithoutTaggedCandidates(answer, dataTag, decoy ? unusedEndPoint : null);
        left.SetRemoteAnswer(signaledAnswer);
        var remoteOffer = SdpSessionDescription.Parse(signaledOffer);
        var remoteAnswer = SdpSessionDescription.Parse(signaledAnswer);
        foreach (var offerer in new[] { false, true })
            Check(SdpNegotiation.ValidateOpusAnswer(remoteOffer, remoteAnswer, offerer).RemoteCandidates.Count == 0,
                "Empty BUNDLE tag fell back to a non-tag candidate");

        void Trickle()
        {
            left.AddRemoteCandidate(new IceCandidate(rightEndPoint).ToSdpAttribute());
            right.AddRemoteCandidate(new IceCandidate(leftEndPoint).ToSdpAttribute());
        }
        if (beforeConnect) Trickle();
        var connectingLeft = left.ConnectAsync(ct);
        var connectingRight = right.ConnectAsync(ct);
        if (!beforeConnect)
        {
            await Task.Delay(40, ct);
            Check(left.State == PeerConnectionState.Connecting && right.State == PeerConnectionState.Connecting,
                "An empty candidate list did not remain open for trickle");
            Check(left.GetDiagnostics().Ice.SelectedRemoteEndPoint == null && right.GetDiagnostics().Ice.SelectedRemoteEndPoint == null,
                "A non-tag route was selected before tagged candidates arrived");
            Trickle();
        }
        await Task.WhenAll(connectingLeft, connectingRight);
        Check(left.GetDiagnostics().Ice.SelectedRemoteEndPoint!.Equals(rightEndPoint) &&
            right.GetDiagnostics().Ice.SelectedRemoteEndPoint!.Equals(leftEndPoint), "Unexpected trickled route");
        Check(unusedRoute.Available == 0, "ICE sent a check to an ignored non-tag candidate");
        var local = await left.OpenDataChannelAsync(new("trickle", "", true, DataChannelReliability.Reliable, 0, 256), ct);
        var remote = await First(right.AcceptDataChannelsAsync(ct), ct);
        await local.SendTextAsync("late candidates", ct);
        Check((await First(remote.ReceiveMessagesAsync(ct), ct)).GetText() == "late candidates", "Trickled forward DCEP failed");
        await remote.SendTextAsync("ready candidates", ct);
        Check((await First(local.ReceiveMessagesAsync(ct), ct)).GetText() == "ready candidates", "Trickled reverse DCEP failed");
        await left.SendOpusAsync(new byte[] { 0xf8, 0xff, 0xfe }, 960, cancellationToken: ct);
        await right.SendOpusAsync(new byte[] { 0xf8, 0xff, 0xfe }, 1920, cancellationToken: ct);
        Check((await First(right.ReceiveAudioAsync(ct), ct)).Timestamp == 960, "Trickled forward SRTP failed");
        Check((await First(left.ReceiveAudioAsync(ct), ct)).Timestamp == 1920, "Trickled reverse SRTP failed");
    }

    internal static async Task Admission()
    {
        // Filtering previously admitted candidates must not bypass the retained-state cap.
        var allowedPort = 49152;
        await using (var changingPolicy = new PeerConnection(new()
        {
            LocalEndPoint = new(IPAddress.Loopback, 0),
            Ice = new() { MaximumCandidatePairs = 1 },
            CandidateFilter = c => c.EndPoint.Port == allowedPort,
        }))
        await using (var remote = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) }))
        {
            changingPolicy.CompleteGathering(); remote.CompleteGathering();
            var policyOffer = changingPolicy.CreateOffer();
            var policyAnswer = remote.CreateAnswer(WithoutTaggedCandidates(policyOffer, false, null));
            changingPolicy.SetRemoteAnswer(WithoutTaggedCandidates(policyAnswer, false, null));
            changingPolicy.AddRemoteCandidate(new IceCandidate(new(IPAddress.Loopback, allowedPort)).ToSdpAttribute());
            allowedPort++;
            Reject<ArgumentException>(() => changingPolicy.AddRemoteCandidate(new IceCandidate(new(IPAddress.Loopback, allowedPort)).ToSdpAttribute()));
        }
        // The cap counts SDP plus trickle, not independent caps per input source.
        await using (var withSdp = new PeerConnection(new()
        {
            LocalEndPoint = new(IPAddress.Loopback, 0), Ice = new() { MaximumCandidatePairs = 1 },
        }))
        await using (var remote = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) }))
        {
            withSdp.CompleteGathering(); remote.CompleteGathering();
            var sdpAnswer = remote.CreateAnswer(withSdp.CreateOffer());
            withSdp.SetRemoteAnswer(sdpAnswer);
            withSdp.AddRemoteCandidate(new IceCandidate(EndPoint(sdpAnswer)).ToSdpAttribute());
            var otherPort = EndPoint(sdpAnswer).Port == 49152 ? 49153 : 49152;
            Reject<ArgumentException>(() => withSdp.AddRemoteCandidate(new IceCandidate(new(IPAddress.Loopback, otherPort)).ToSdpAttribute()));
        }
        var options = new PeerConnectionOptions
        {
            LocalEndPoint = new(IPAddress.Loopback, 0),
            Ice = new() { MaximumCandidatePairs = 1 },
            CandidateFilter = c => c.EndPoint.Port != 49153,
        };
        await using var left = new PeerConnection(options);
        await using var right = new PeerConnection(options);
        var candidate = new IceCandidate(new(IPAddress.Loopback, 49152)).ToSdpAttribute();
        Reject<InvalidOperationException>(() => left.AddRemoteCandidate(candidate));
        left.CompleteGathering(); right.CompleteGathering();
        var offer = left.CreateOffer();
        Reject<InvalidOperationException>(() => left.AddRemoteCandidate(candidate));
        var answer = right.CreateAnswer(WithoutTaggedCandidates(offer, false, null));
        left.SetRemoteAnswer(WithoutTaggedCandidates(answer, false, null));
        for (var duplicate = 0; duplicate < 100; duplicate++) left.AddRemoteCandidate(candidate);
        Reject<ArgumentException>(() => left.AddRemoteCandidate(new IceCandidate(new(IPAddress.Loopback, 49154)).ToSdpAttribute()));
        Reject<NotSupportedException>(() => left.AddRemoteCandidate(new IceCandidate(new(IPAddress.Loopback, 49153)).ToSdpAttribute()));
        Reject<NotSupportedException>(() => left.AddRemoteCandidate(new IceCandidate(new(IPAddress.IPv6Loopback, 49152)).ToSdpAttribute()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Reject<OperationCanceledException>(() => left.ConnectAsync(cancelled.Token));
        Check(left.State == PeerConnectionState.Ready, "Cancelled start consumed the ready state");
        await left.DisposeAsync();
        Reject<ObjectDisposedException>(() => left.AddRemoteCandidate(candidate));
        Check(left.GetEstablishmentEvidence().Phases.All(p => p.Status == EstablishmentStatus.NotStarted),
            "Buffered candidates started a transport without ConnectAsync");
    }
}
