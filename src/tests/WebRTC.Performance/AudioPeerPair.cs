using System.Net;
using System.Diagnostics;
using tryAGI.WebRTC;

// Benchmark-owned callers keep their buffers valid until every asynchronous send completes.
internal sealed class AudioPeerPair : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime;
    internal double ConstructionMilliseconds { get; private set; }
    internal double SignalingMilliseconds { get; private set; }
    internal PeerConnection Left { get; }
    internal PeerConnection Right { get; }
    private readonly byte[] leftPayload, rightPayload;
    private readonly IAsyncEnumerator<EncodedOpusPacket> fromLeft, fromRight;
    private uint leftTimestamp, rightTimestamp;
    private long allowedRightDrops;
    private AudioPeerPair(PeerConnection left, PeerConnection right, bool trace, int payload, int identity, CancellationToken ct)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Left = left; Right = right;
        if (trace)
        {
            var options = new PeerDiagnosticsOptions { Metrics = false, PacketTrace = true, EventCapacity = 512,
                CaptureDuration = TimeSpan.FromMinutes(5) };
            Left.AttachDiagnostics(options); Right.AttachDiagnostics(options);
        }
        leftPayload = new byte[payload]; rightPayload = new byte[payload];
        leftPayload.AsSpan().Fill((byte)(identity * 2 + 1)); rightPayload.AsSpan().Fill((byte)(identity * 2 + 2));
        // Synthetic encoded bytes exercise routing/ownership, not Opus decoder quality.
        leftPayload[0] = 0xf8; rightPayload[0] = 0xf8;
        fromLeft = Right.ReceiveAudioAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        fromRight = Left.ReceiveAudioAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
    }
    internal static async Task<AudioPeerPair> Create(SrtpProfile profile, bool trace, bool channels,
        int payload, int identity, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        PeerConnection? left = null, right = null;
        AudioPeerPair? pair = null;
        try
        {
            PeerConnection Make() => new(new() { LocalEndPoint = new(IPAddress.Loopback, 0), DataChannels = channels,
                Dtls = new() { Profiles = [profile] }, AudioQueueCapacity = 8 });
            left = Make(); right = Make();
            pair = new AudioPeerPair(left, right, trace, payload, identity, ct);
            pair.ConstructionMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            var offer = pair.Left.CreateOffer(); var answer = pair.Right.CreateAnswer(offer); pair.Left.SetRemoteAnswer(answer);
            pair.SignalingMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await Task.WhenAll(pair.Left.ConnectAsync(ct), pair.Right.ConnectAsync(ct));
            return pair;
        }
        catch
        {
            if (pair is not null) await pair.DisposeAsync();
            else await Task.WhenAll(left?.DisposeAsync().AsTask() ?? Task.CompletedTask, right?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            throw;
        }
    }

    internal Task Exchange(int packets) => Task.WhenAll(Direction(true, packets), Direction(false, packets));
    private async Task Direction(bool left, int packets)
    {
        var sender = left ? Left : Right; var reader = left ? fromLeft : fromRight;
        var payload = left ? leftPayload : rightPayload;
        for (var n = 0; n < packets; n++)
        {
            var timestamp = left ? leftTimestamp += 960 : rightTimestamp += 960;
            await sender.SendOpusAsync(payload, timestamp, cancellationToken: lifetime.Token);
            if (!await reader.MoveNextAsync() || reader.Current.Timestamp != timestamp ||
                !reader.Current.Payload.AsSpan().SequenceEqual(payload)) throw new IOException("Bidirectional audio ownership/routing mismatch");
        }
    }
    internal void Check()
    {
        var left = Left.GetDiagnostics(); var right = Right.GetDiagnostics();
        if (left.RejectedAudioPackets != 0 || right.RejectedAudioPackets != 0 ||
            left.Dtls?.RejectedRecords != 0 || right.Dtls?.RejectedRecords != 0 ||
            left.DroppedAudioPackets != 0 || right.DroppedAudioPackets != allowedRightDrops)
            throw new IOException("Audio fixture had unexpected rejection or queue drops");
    }
    internal async Task CancelledSend()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await Left.SendOpusAsync(leftPayload, leftTimestamp + 960, cancellationToken: cancelled.Token); }
        catch (OperationCanceledException) { return; }
        throw new IOException("Already-cancelled audio send completed");
    }
    internal async Task<long> SlowReader()
    {
        Check();
        byte[]? retained = null;
        await Left.SendOpusAsync(leftPayload, leftTimestamp += 960, cancellationToken: lifetime.Token);
        if (!await fromLeft.MoveNextAsync()) throw new IOException("Missing retained audio");
        retained = fromLeft.Current.Payload;
        for (var i = 0; i < 48; i++)
        {
            await Left.SendOpusAsync(leftPayload, leftTimestamp += 960, cancellationToken: lifetime.Token);
            if (i % 4 == 0) await Task.Delay(1, lifetime.Token);
        }
        while (Right.GetDiagnostics().DroppedAudioPackets == allowedRightDrops) await Task.Delay(1, lifetime.Token);
        for (var i = 0; ; i++)
        {
            if (i > 48 || !await fromLeft.MoveNextAsync() || !fromLeft.Current.Payload.AsSpan().SequenceEqual(leftPayload))
                throw new IOException("Slow reader corrupted retained audio");
            if (fromLeft.Current.Timestamp == leftTimestamp) break;
        }
        if (!retained.AsSpan().SequenceEqual(leftPayload)) throw new IOException("Later delivery mutated owned audio");
        var drops = Right.GetDiagnostics().DroppedAudioPackets - allowedRightDrops;
        allowedRightDrops += drops;
        return drops;
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try { await Task.WhenAll(Left.DisposeAsync().AsTask(), Right.DisposeAsync().AsTask()); }
        finally
        {
            await fromLeft.DisposeAsync(); await fromRight.DisposeAsync(); lifetime.Dispose();
        }
    }
}
