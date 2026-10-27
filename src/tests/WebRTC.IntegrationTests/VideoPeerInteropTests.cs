using System.Net.Http.Json;
using tryAGI.WebRTC;

internal static partial class VideoPeerTests
{
    private static async Task<EncodedVideoFrame> VideoAtTimestamp(
        IAsyncEnumerable<EncodedVideoFrame> frames, uint timestamp, CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.WithCancellation(cancellationToken))
            if (frame.Timestamp == timestamp) return frame;
        throw new IOException($"Video stream ended before expected timestamp {timestamp}.");
    }

    internal static async Task FinalFramePublication()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var frames = System.Threading.Channels.Channel.CreateUnbounded<EncodedVideoFrame>();
        var middle = new EncodedVideoFrame(VideoCodec.H264, 1, 3000, 1, 1, true, [1]);
        var final = middle with { Timestamp = 6000, Payload = [2] };
        await frames.Writer.WriteAsync(middle, deadline.Token);
        var pending = VideoAtTimestamp(frames.Reader.ReadAllAsync(deadline.Token), 6000, deadline.Token);
        Check(!pending.IsCompleted); // The earlier frame must not satisfy final-frame admission.
        await frames.Writer.WriteAsync(final, deadline.Token);
        Check(ReferenceEquals(final, await pending));

        frames.Writer.Complete();
        try { await VideoAtTimestamp(frames.Reader.ReadAllAsync(), 9000, deadline.Token); throw new InvalidOperationException("Missing final frame accepted"); }
        catch (IOException) { }
        var absent = System.Threading.Channels.Channel.CreateUnbounded<EncodedVideoFrame>();
        using var canceled = new CancellationTokenSource();
        var wait = VideoAtTimestamp(absent.Reader.ReadAllAsync(canceled.Token), 6000, canceled.Token);
        canceled.Cancel();
        try { await wait; throw new IOException("Missing final frame ignored cancellation"); }
        catch (OperationCanceledException) { }
    }

    internal static async Task Pion(Uri uri, VideoCodec codec, bool offerer, bool passive, bool rtcp = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await using var peer = new PeerConnection(Options(codec, SrtpProfile.AeadAes128Gcm) with { Rtcp = new() { PointToPoint = true } }); string? id = null;
        var kind = codec == VideoCodec.H264 ? "h264" : "vp8";
        try
        {
            var request = new SessionRequest(offerer ? peer.CreateOffer() : "", passive, Video: kind, Rtcp: rtcp);
            using var response = await http.PostAsJsonAsync(new Uri(uri, offerer ? "/session/answer" : "/session/offer"), request, InteropJson.Default.SessionRequest, ct);
            response.EnsureSuccessStatusCode();
            var remote = (await response.Content.ReadFromJsonAsync(InteropJson.Default.SessionResponse, ct))!; id = remote.Id;
            if (offerer) peer.SetRemoteAnswer(remote.Sdp);
            else
            {
                var answer = peer.CreateAnswer(remote.Sdp, passive ? SdpSetup.Passive : SdpSetup.Active);
                using var accepted = await http.PostAsJsonAsync(new Uri(uri, $"/session/{id}/answer"), new SessionRequest(answer, false, Video: kind), InteropJson.Default.SessionRequest, ct);
                accepted.EnsureSuccessStatusCode();
            }
            await peer.ConnectAsync(ct); Check(peer.VideoFormat!.Codec == codec && peer.CanSendVideo);
            await peer.SendOpusAsync(new byte[] { 0xf8, 0xff, 0xfe }, 48000, cancellationToken: ct);
            Check((await First(peer.ReceiveAudioAsync(ct), ct)).Timestamp == 48000);
            await peer.SendVideoRtpAsync(codec == VideoCodec.H264 ? new byte[] { 0x61, 0x11 } : new byte[] { 0x10, 1, 0, 0 }, 0, true, ct);
            if (rtcp)
            {
                var feedback = await First(peer.ReceiveVideoKeyFrameRequestsAsync(ct), ct);
                Check(feedback.MediaSource == peer.VideoSource && feedback.SenderSource == 0x12345678);
            }
            await peer.SendVideoRtpAsync(codec == VideoCodec.H264 ? new byte[] { 0x65, 0x22, 0x33 } : new byte[] { 0x10, 1, 2, 3 }, 3000, true, ct);
            await peer.SendVideoRtpAsync(codec == VideoCodec.H264 ? new byte[] { 0x65, 0x44, 0x55 } : new byte[] { 0x10, 1, 4, 5 }, 6000, true, ct);
            while (peer.GetVideoDiagnostics().CompletedFrames < (codec == VideoCodec.H264 ? 2 : 3)) await Task.Delay(5, ct);
            // Assembly counters precede channel publication; consume until the final
            // echoed timestamp instead of treating a diagnostic counter as a barrier.
            var video = await VideoAtTimestamp(peer.ReceiveVideoAsync(ct), 6000, ct);
            Check(video.Timestamp == 6000 && video.Codec == codec && video.Payload.SequenceEqual(codec == VideoCodec.H264 ? new byte[] { 0, 0, 0, 1, 0x65, 0x44, 0x55 } : new byte[] { 1, 4, 5 }));
            var stats = (await http.GetFromJsonAsync(new Uri(uri, $"/session/{id}/stats"), InteropJson.Default.SessionStats, ct))!;
            Check(stats is { Audio: 1, Video: 3, Failures: 0 });
            if (rtcp)
            {
                peer.RequestVideoKeyFrame(video.SynchronizationSource);
                while (stats is not { Reports: > 0, PictureLoss: > 0, SentPictureLoss: 1, Cnames: > 0 })
                {
                    if (deadline.IsCancellationRequested) throw new IOException($"Independent RTCP counters: {stats}; owned: {peer.GetRtcpDiagnostics()}");
                    await Task.Delay(10, ct);
                    stats = (await http.GetFromJsonAsync(new Uri(uri, $"/session/{id}/stats"), InteropJson.Default.SessionStats, ct))!;
                }
                Check(stats.Failures == 0 && peer.GetRtcpDiagnostics() is { ReceivedPictureLoss: 1, SentPictureLoss: > 0 });
            }
        }
        finally
        { if (id != null) { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var deleted = await http.DeleteAsync(new Uri(uri, $"/session/{id}"), cleanup.Token); deleted.EnsureSuccessStatusCode(); } }
    }
}
