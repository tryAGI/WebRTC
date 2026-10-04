using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using tryAGI.WebRTC;

internal static class RtcpPeerTests
{
    private static readonly byte[] Opus = [0xf8, 0xff, 0xfe];
    private static void Check(bool value, string message = "Automatic RTCP peer assertion failed") { if (!value) throw new IOException(message); }
    private static PeerConnectionOptions Options(SrtpProfile profile = SrtpProfile.AeadAes128Gcm, bool video = true) => new()
    { LocalEndPoint = new(IPAddress.Loopback, 0), DataChannels = false, ConnectionTimeout = TimeSpan.FromSeconds(5),
        VideoCodecs = video ? [SdpVideoTests.H264()] : [], VideoDirection = SdpDirection.SendReceive,
        Dtls = new() { Profiles = [profile] }, Video = new() { MaximumFrameAge = TimeSpan.FromMilliseconds(75) },
        Rtcp = new() { PointToPoint = true, MinimumPictureLossInterval = TimeSpan.FromMilliseconds(200) } };
    private static async Task Connect(PeerConnection left, PeerConnection right, CancellationToken ct, SdpSetup setup = SdpSetup.Active, bool reduced = true)
    {
        var offer = left.CreateOffer(); if (!reduced) offer = offer.Replace("a=rtcp-rsize\r\n", "");
        left.SetRemoteAnswer(right.CreateAnswer(offer, setup)); await Task.WhenAll(left.ConnectAsync(ct), right.ConnectAsync(ct));
    }
    private static async Task<T> First<T>(IAsyncEnumerable<T> items, CancellationToken ct)
    { await foreach (var item in items.WithCancellation(ct)) return item; throw new IOException("RTCP stream ended unexpectedly"); }
    private static async Task Until(Func<bool> condition, CancellationToken ct) { while (!condition()) await Task.Delay(5, ct); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new IOException("Unsafe RTCP API accepted"); }
    private static async Task<IReadOnlyList<RtcpPacket>> Control(PeerConnection peer, Func<IReadOnlyList<RtcpPacket>, bool> match, CancellationToken ct)
    {
        await foreach (var data in peer.ReceiveRtcpAsync(ct))
        {
            Check(RtcpPackets.TryParse(data, out var packets, out var compound) && compound, "Automatic reports must retain compound CNAME");
            if (match(packets)) return packets;
        }
        throw new IOException("Automatic RTCP stream ended");
    }
    internal static async Task Reports(SrtpProfile profile, SdpSetup setup)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        await using var left = new PeerConnection(Options(profile)); await using var right = new PeerConnection(Options(profile));
        await Connect(left, right, ct, setup);
        var initial = await Control(right, p => p.OfType<RtcpReceiverReport>().Count() == 2, ct);
        Check(initial.OfType<RtcpSourceDescription>().Single().Chunks.All(c => c.CanonicalName == left.CanonicalName));
        for (uint n = 0; n < 4; n++) await left.SendOpusAsync(Opus, 960 * n, cancellationToken: ct);
        await Until(() => right.GetRtcpDiagnostics()!.ReceptionSources == 1, ct);
        await left.SendVideoRtpAsync(new byte[] { 0x61, 0x11 }, 0, true, ct);
        await left.SendVideoRtpAsync(new byte[] { 0x65, 0x22, 0x33 }, 3000, true, ct);
        await Until(() => right.GetVideoDiagnostics().CompletedFrames == 1, ct);
        var reports = await Control(right, p => p.OfType<RtcpSenderReport>().Count() == 2, ct);
        var audio = reports.OfType<RtcpSenderReport>().Single(r => r.SenderSource == left.AudioSource);
        var video = reports.OfType<RtcpSenderReport>().Single(r => r.SenderSource == left.VideoSource);
        Check(audio.PacketCount == 4 && audio.OctetCount == 12 && video.PacketCount == 2 && video.OctetCount == 5);
        var utc = RtcpClock.ToNtpTimestamp(DateTimeOffset.UtcNow);
        Check(utc >= audio.NtpTimestamp && utc - audio.NtpTimestamp < (2UL << 32) && audio.NtpTimestamp == video.NtpTimestamp, "Stale or inconsistent automatic SR clock");
        var reception = await Control(left, p => p.OfType<RtcpReceiverReport>().SelectMany(r => r.Reports).Any(r => r.Source == left.AudioSource && r.LastSenderReport != 0), ct);
        var block = reception.OfType<RtcpReceiverReport>().SelectMany(r => r.Reports).Single(r => r.Source == left.AudioSource);
        Check(block.CumulativeLost == 0 && block.FractionLost == 0 && block.HighestSequence != 0 && block.DelaySinceLastSenderReport != 0);
        await Until(() => left.GetRtcpDiagnostics()!.RoundTripTime != null, ct);
        Check(left.GetRtcpDiagnostics()!.RoundTripTime >= TimeSpan.Zero && left.GetRtcpDiagnostics()!.RoundTripTime < TimeSpan.FromSeconds(2));
        Check(left.GetDiagnostics().RejectedControlPackets == 0 && right.GetDiagnostics().RejectedControlPackets == 0);
    }
    internal static async Task Feedback(bool reduced, bool delayedObservation = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        await using var left = new PeerConnection(Options()); await using var right = new PeerConnection(Options());
        await Connect(left, right, ct, reduced: reduced);
        await left.SendVideoRtpAsync(new byte[] { 0x61, 0x11 }, 0, true, ct);
        var first = await First(left.ReceiveVideoKeyFrameRequestsAsync(ct), ct);
        Check(first.MediaSource == left.VideoSource && first.SenderSource == right.VideoSource);
        await Until(() => right.GetRtcpDiagnostics()!.SentPictureLoss == 3, ct);
        await Task.Delay(350, ct); Check(right.GetRtcpDiagnostics()!.SentPictureLoss == 3, "Unbounded automatic PLI retries");
        // A complete encoded key clears the bootstrap request. Decoder failure can start another bounded episode.
        await left.SendVideoRtpAsync(new byte[] { 0x65, 0x22, 0x33 }, 3000, true, ct);
        await Until(() => right.GetVideoDiagnostics().CompletedFrames == 1, ct);
        var prior = right.GetRtcpDiagnostics()!.SentPictureLoss;
        // Submission precedes the first send, so this gives a conservative lower
        // bound for the next permitted send. Observation of the counter can be late.
        var requestedAt = Stopwatch.GetTimestamp();
        right.RequestVideoKeyFrame(left.VideoSource);
        await Until(() => right.GetRtcpDiagnostics()!.SentPictureLoss > prior, ct);
        prior = right.GetRtcpDiagnostics()!.SentPictureLoss;
        await left.SendVideoRtpAsync(new byte[] { 0x65, 0x44, 0x55 }, 6000, true, ct);
        await Until(() => right.GetVideoDiagnostics().CompletedFrames == 2, ct);
        for (var n = 0; n < 20; n++) right.RequestVideoKeyFrame(left.VideoSource);
        // A delayed test continuation must not classify an allowed retry as an
        // early send. Validate the count only inside the conservative window.
        if (delayedObservation) await Task.Delay(250, ct);
        while (Stopwatch.GetElapsedTime(requestedAt) < TimeSpan.FromMilliseconds(200))
        {
            var count = right.GetRtcpDiagnostics()!.SentPictureLoss;
            var elapsed = Stopwatch.GetElapsedTime(requestedAt);
            if (elapsed < TimeSpan.FromMilliseconds(200))
                Check(count == prior, $"Key completion/reset bypassed PLI throttle after {elapsed.TotalMilliseconds:F1} ms");
            await Task.Delay(5, ct);
        }
        await Until(() => right.GetRtcpDiagnostics()!.SentPictureLoss > prior, ct);
        await left.SendVideoRtpAsync(new byte[] { 0x65, 0x11 }, 9000, true, ct);
        await Until(() => right.GetVideoDiagnostics().CompletedFrames == 3, ct);
        prior = right.GetRtcpDiagnostics()!.SentPictureLoss; await Task.Delay(300, ct); Check(right.GetRtcpDiagnostics()!.SentPictureLoss == prior);
        // Incomplete FU expires while RTP is silent, and causes a new request.
        await left.SendVideoRtpAsync(new byte[] { 0x7c, 0x85, 0x11 }, 12000, false, ct);
        await Until(() => right.GetVideoDiagnostics().DroppedFrames > 0, ct);
        await Until(() => right.GetRtcpDiagnostics()!.SentPictureLoss > prior, ct);
        await Reject<ArgumentException>(() => Task.Run(() => right.RequestVideoKeyFrame(left.AudioSource)));
        await Reject<ArgumentException>(() => left.SendRtcpAsync(RtcpPackets.Encode([new RtcpPictureLossIndication(left.VideoSource, right.VideoSource)]), ct).AsTask());
    }
    internal static async Task BudgetAndCancellation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        await using var left = new PeerConnection(Options(video: false) with { Rtcp = new() { MaximumControlBytesPerSecond = 64, SessionBandwidthBitsPerSecond = 16000 } });
        await using var right = new PeerConnection(Options(video: false)); await Connect(left, right, ct);
        // Fill the one-datagram burst with a valid, locally owned report. Payload snapshot precedes budget wait.
        var blocks = Enumerable.Range(1, 31).Select(i => new RtcpReceptionReport((uint)i, 0, 0, 0, 0, 0, 0)).ToArray();
        var large = RtcpPackets.Encode([new RtcpReceiverReport(left.AudioSource, blocks), new RtcpSourceDescription([new(left.AudioSource, left.CanonicalName)])]);
        await left.SendRtcpAsync(large, ct);
        using var cancel = new CancellationTokenSource(); var pending = new List<Task>();
        for (var n = 0; n < 8; n++) pending.Add(left.SendRtcpAsync(large, cancel.Token).AsTask());
        await Reject<InvalidOperationException>(() => left.SendRtcpAsync(large, ct).AsTask());
        var start = Stopwatch.GetTimestamp(); await left.SendOpusAsync(Opus, 1234, cancellationToken: ct);
        Check((await First(right.ReceiveAudioAsync(ct), ct)).Timestamp == 1234 && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(1), "RTCP budget blocked audio");
        cancel.Cancel(); foreach (var request in pending) await Reject<OperationCanceledException>(() => request);
        var bare = RtcpPackets.Encode([new RtcpReceiverReport(left.AudioSource, [])]);
        var snapshot = bare.ToArray();
        while (true)
        {
            try { var queued = left.SendRtcpAsync(bare, ct).AsTask(); await Task.Yield(); if (queued.IsFaulted) await queued; Array.Fill<byte>(bare, 0); await queued; break; }
            catch (InvalidOperationException error)
            {
                if (left.State == PeerConnectionState.Failed) throw new IOException("RTCP worker failed", await left.Completion);
                if (error.Message != "The bounded RTCP send queue is full.") throw;
                await Task.Delay(5, ct);
            }
        }
        await foreach (var received in right.ReceiveRtcpAsync(ct)) if (received.SequenceEqual(snapshot)) break;
        var waiting = left.SendRtcpAsync(large, ct).AsTask(); await left.DisposeAsync();
        await Reject<Exception>(() => waiting); Check(left.State == PeerConnectionState.Closed);
    }
    internal static async Task RegularDeadline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)); var ct = timeout.Token;
        await using var left = new PeerConnection(Options(video: false) with { Rtcp = new() { MaximumControlBytesPerSecond = 128, SessionBandwidthBitsPerSecond = 32000 } });
        await using var right = new PeerConnection(Options(video: false)); await Connect(left, right, ct);
        var blocks = Enumerable.Range(1, 31).Select(i => new RtcpReceptionReport((uint)i, 0, 0, 0, 0, 0, 0)).ToArray();
        var large = RtcpPackets.Encode([new RtcpReceiverReport(left.AudioSource, blocks), new RtcpSourceDescription([new(left.AudioSource, left.CanonicalName)])]);
        await left.SendRtcpAsync(large, ct); using var cancel = new CancellationTokenSource();
        var waiting = left.SendRtcpAsync(large, cancel.Token).AsTask();
        using var reportDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); reportDeadline.CancelAfter(TimeSpan.FromSeconds(4));
        var automatic = await Control(right, p => p.OfType<RtcpReceiverReport>().Any(r => r.Reports.Count == 0), reportDeadline.Token);
        Check(automatic.Count == 2 && !waiting.IsCompleted, "Large manual send postponed regular RTCP or exhausted its budget early");
        cancel.Cancel(); await Reject<OperationCanceledException>(() => waiting);
    }
    internal static async Task Admission(bool reduced, bool pli)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)); var ct = timeout.Token;
        await using var peer = new PeerConnection(Options()); await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var offerText = SdpNegotiation.CreateOffer(new(ice.LocalCredentials, identity.GetFingerprintSha256(), ice.LocalEndPoint, canonicalName: "remote"), 1234, 2345, [SdpVideoTests.H264()], false, videoDirection: SdpDirection.SendReceive);
        if (!reduced) offerText = offerText.Replace("a=rtcp-rsize\r\n", ""); if (!pli) offerText = offerText.Replace("a=rtcp-fb:102 nack pli\r\n", "");
        var offer = SdpSessionDescription.Parse(offerText); var answer = SdpSessionDescription.Parse(peer.CreateAnswer(offerText));
        var session = SdpNegotiation.ValidateAnswer(offer, answer, true); var connecting = peer.ConnectAsync(ct);
        await ice.ConnectAsync(session.RemoteCredentials, session.IceRole, session.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), ct);
        await using var dtls = new DtlsSrtpTransport(ice, identity, session.DtlsRole, Convert.FromHexString(session.RemoteFingerprintSha256));
        await Task.WhenAll(connecting, dtls.ConnectAsync(ct));
        byte[] Feedback(uint target, uint sender = 2345) => RtcpPackets.Encode([new RtcpReceiverReport(sender, []), new RtcpSourceDescription([new(sender, "remote")]), new RtcpPictureLossIndication(sender, target)]);
        var invalid = new[] { Feedback(peer.AudioSource), Feedback(9999), Feedback(peer.VideoSource, peer.VideoSource) };
        foreach (var data in invalid) await dtls.SendRtcpAsync(data, ct);
        await Until(() => peer.GetRtcpDiagnostics()!.RejectedPackets == 3, ct);
        Check(peer.GetRtcpDiagnostics()!.ReceivedPictureLoss == 0 && peer.GetRtcpDiagnostics()!.ReceivedPackets == 0, "Rejected compound leaked report/feedback effects");
        await dtls.SendRtcpAsync(RtcpPackets.Encode([new RtcpPictureLossIndication(2345, peer.VideoSource)]), ct);
        if (reduced && pli) await Until(() => peer.GetRtcpDiagnostics()!.ReceivedPictureLoss == 1, ct);
        else await Until(() => peer.GetRtcpDiagnostics()!.RejectedPackets == 4, ct);
        await dtls.SendRtcpAsync(Feedback(peer.VideoSource), ct);
        if (pli)
        {
            await Until(() => peer.GetRtcpDiagnostics()!.ReceivedPackets >= 1, ct);
            Check((await First(peer.ReceiveVideoKeyFrameRequestsAsync(ct), ct)).MediaSource == peer.VideoSource);
        }
        else await Until(() => peer.GetRtcpDiagnostics()!.RejectedPackets == 5, ct);
        // A new admitted CNAME group disables the opt-in early topology; it does not establish identity.
        await dtls.SendRtcpAsync(RtcpPackets.Encode([new RtcpReceiverReport(2345, []), new RtcpSourceDescription([new(2345, "remote")])]), ct);
        await dtls.SendRtcpAsync(RtcpPackets.Encode([new RtcpReceiverReport(1234, []), new RtcpSourceDescription([new(1234, "another-group")])]), ct);
        await Until(() => !peer.GetRtcpDiagnostics()!.EarlyFeedbackEnabled, ct);
        var before = peer.GetRtcpDiagnostics()!.RejectedPackets;
        await dtls.SendRtcpAsync(RtcpPackets.Encode([new RtcpReceiverReport(1234, []), new RtcpSourceDescription([new(1234, "changed-name")])]), ct);
        await Until(() => peer.GetRtcpDiagnostics()!.RejectedPackets > before, ct);
        var acceptedBefore = peer.GetRtcpDiagnostics()!.ReceivedPackets; var rejectedBefore = peer.GetRtcpDiagnostics()!.RejectedPackets;
        for (uint n = 0; n < 9; n++)
            await dtls.SendRtcpAsync(RtcpPackets.Encode([new RtcpReceiverReport(5000 + n, []), new RtcpSourceDescription([new(5000 + n, "remote")])]), ct);
        await Until(() => peer.GetRtcpDiagnostics()!.ReceivedPackets == acceptedBefore + 8 && peer.GetRtcpDiagnostics()!.RejectedPackets == rejectedBefore + 1, ct);
        Check(peer.GetRtcpDiagnostics()!.ReceptionSources == 0, "Control-only reporters consumed RTP reception state");
        // Exactly 64 admitted CNAME entries fit; a 65th rejects the entire compound.
        var extraNames = Enumerable.Range(0, 54).Select(n => new RtcpSdesChunk((uint)(6000 + n), "remote")).ToArray();
        await dtls.SendRtcpAsync(RtcpPackets.Encode([new RtcpReceiverReport(2345, []),
            new RtcpSourceDescription(new[] { new RtcpSdesChunk(2345, "remote") }.Concat(extraNames.Take(30)).ToArray()),
            new RtcpSourceDescription(extraNames.Skip(30).ToArray())]), ct);
        await Until(() => peer.GetRtcpDiagnostics()!.ReceivedPackets == acceptedBefore + 9, ct);
        await dtls.SendRtcpAsync(RtcpPackets.Encode([new RtcpReceiverReport(2345, []), new RtcpSourceDescription([new(2345, "remote"), new(7000, "remote")])]), ct);
        await Until(() => peer.GetRtcpDiagnostics()!.RejectedPackets == rejectedBefore + 2, ct);
        Check(peer.State == PeerConnectionState.Connected);
        if (!pli) await Reject<NotSupportedException>(() => Task.Run(() => peer.RequestVideoKeyFrame(2345)));
    }
}
