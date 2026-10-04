using System.Net;
using tryAGI.WebRTC;

internal static partial class VideoPeerTests
{
    private static void Check(bool value) { if (!value) throw new IOException("Video peer assertion failed."); }
    private static async Task<T> First<T>(IAsyncEnumerable<T> items, CancellationToken ct)
    { await foreach (var item in items.WithCancellation(ct)) return item; throw new IOException("Video stream ended."); }
    private static PeerConnectionOptions Options(VideoCodec codec, SrtpProfile profile, SdpDirection direction = SdpDirection.SendReceive) => new()
    { LocalEndPoint = new(IPAddress.Loopback, 0), DataChannels = false, VideoCodecs = [codec == VideoCodec.H264 ? SdpVideoTests.H264() : SdpVideoTests.Vp8()],
        VideoDirection = direction, Dtls = new() { Profiles = [profile] }, ConnectionTimeout = TimeSpan.FromSeconds(5), Video = new() { QueueCapacity = 1, MaximumFrameAge = TimeSpan.FromMilliseconds(100) } };
    internal static async Task Local(VideoCodec codec, SrtpProfile profile, SdpSetup setup, TurnServerTransport? relay = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var udp = relay == TurnServerTransport.Udp ? new TurnFixture(modern: true) : null;
        await using var stream = relay is TurnServerTransport.Tcp or TurnServerTransport.Tls ? new TurnStreamFixture(relay == TurnServerTransport.Tls) : null;
        await using var left = new PeerConnection(Options(codec, profile) with { Ice = new() { RelayOnly = relay != null } }); await using var right = new PeerConnection(Options(codec, profile));
        if (relay != null) await left.GatherRelayCandidateAsync(udp?.Server ?? stream!.Server, new(TurnFixture.Username, TurnFixture.Secret), stream?.Options ?? TurnFixture.Fast(), ct);
        left.SetRemoteAnswer(right.CreateAnswer(left.CreateOffer(), setup)); await Task.WhenAll(left.ConnectAsync(ct), right.ConnectAsync(ct));
        if (relay != null) Check(left.GetDiagnostics().Ice.SelectedLocalEndPoint!.Equals(left.GetLocalCandidates().Single(c => c.Type == IceCandidateType.Relay).EndPoint));
        Check(left.VideoFormat!.Codec == codec && left.CanSendVideo);
        await left.SendOpusAsync(new byte[] { 0xf8, 0xff, 0xfe }, 48000, cancellationToken: ct);
        Check((await First(right.ReceiveAudioAsync(ct), ct)).SynchronizationSource == left.AudioSource);
        var bootstrap = codec == VideoCodec.H264 ? new byte[] { 0x61, 0x11 } : new byte[] { 0x10, 1, 0, 0 };
        await left.SendVideoRtpAsync(bootstrap, 0, true, ct);
        var payload = codec == VideoCodec.H264 ? new byte[] { 0x65, 0x22, 0x33 } : new byte[] { 0x10, 1, 2, 3 };
        for (uint n = 1; n <= 4; n++) await left.SendVideoRtpAsync(payload, 3000 * n, true, ct);
        while (right.GetVideoDiagnostics().CompletedFrames < (codec == VideoCodec.H264 ? 4 : 5) || right.GetVideoDiagnostics().DroppedQueueFrames < 3) await Task.Delay(5, ct);
        var frame = await First(right.ReceiveVideoAsync(ct), ct);
        Check(frame.Timestamp == 12000 && frame.SynchronizationSource == left.VideoSource && frame.Codec == codec && right.GetVideoDiagnostics().DroppedQueueFrames >= 3);
        Check(frame.Payload.SequenceEqual(codec == VideoCodec.H264 ? new byte[] { 0, 0, 0, 1, 0x65, 0x22, 0x33 } : new byte[] { 1, 2, 3 }));
        await right.SendVideoRtpAsync(bootstrap, 0, true, ct); await right.SendVideoRtpAsync(payload, 3000, true, ct);
        while (left.GetVideoDiagnostics().CompletedFrames < (codec == VideoCodec.H264 ? 1 : 2)) await Task.Delay(5, ct);
        Check((await First(left.ReceiveVideoAsync(ct), ct)).SynchronizationSource == right.VideoSource);
        var incomplete = codec == VideoCodec.H264 ? new byte[] { 0x7c, 0x85, 0x11 } : new byte[] { 0x10, 1, 2, 3 };
        await left.SendVideoRtpAsync(incomplete, 15000, false, ct);
        while (right.GetVideoDiagnostics().BufferedBytes == 0) await Task.Delay(5, ct);
        while (right.GetVideoDiagnostics().BufferedBytes != 0) await Task.Delay(5, ct);
        Check(right.GetVideoDiagnostics().DroppedFrames > 0);
        await left.DisposeAsync(); Check(left.GetVideoDiagnostics().BufferedBytes == 0);
        if (relay != null) Check((udp ?? stream!.Backend).Deletes == 1);
    }
    internal static async Task Direction()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var left = new PeerConnection(Options(VideoCodec.Vp8, SrtpProfile.AeadAes128Gcm, SdpDirection.SendOnly));
        await using var right = new PeerConnection(Options(VideoCodec.Vp8, SrtpProfile.AeadAes128Gcm, SdpDirection.ReceiveOnly));
        left.SetRemoteAnswer(right.CreateAnswer(left.CreateOffer())); await Task.WhenAll(left.ConnectAsync(ct), right.ConnectAsync(ct));
        await left.SendVideoRtpAsync(new byte[] { 0x10, 1, 0, 0 }, 1234, true, ct);
        Check((await First(right.ReceiveVideoAsync(ct), ct)).Timestamp == 1234 && !right.CanSendVideo);
        try { await right.SendVideoRtpAsync(new byte[] { 0x10, 1, 0, 0 }, 0, true, ct); throw new IOException("Unnegotiated send accepted"); } catch (InvalidOperationException) { }
        try { await left.SendVideoRtpAsync(new byte[left.MaximumVideoPayloadBytes + 1], 0, true, ct); throw new IOException("Oversize send accepted"); } catch (ArgumentOutOfRangeException) { }
    }
}
