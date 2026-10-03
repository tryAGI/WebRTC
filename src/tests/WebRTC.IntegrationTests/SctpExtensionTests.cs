using tryAGI.WebRTC;

internal static class SctpExtensionTests
{
    private static void Check(bool value, string message = "PR-SCTP assertion failed") => SctpTests.Check(value, message);
    internal static async Task FragmentAbandonment(bool ordered, bool timed, bool wrap)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = SctpTests.Fast() with { InitialTransmissionSequenceNumber = wrap ? uint.MaxValue - 1 : null,
            InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(timed ? 500 : 100) };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        Check(pair.Left.SupportsPartialReliability && pair.Right.SupportsPartialReliability);
        pair.Proxy.DropLeftApplications = 2;
        // Three fragments plus later DATA cannot trigger three fast-retransmit
        // reports, so the timed case exercises expiration before retransmission.
        var data = new byte[timed ? 2500 : 8192]; Array.Fill(data, (byte)0xaa);
        var policy = new SctpReliability(timed ? DataChannelReliability.Timed : DataChannelReliability.RetransmissionLimited, timed ? 250u : 0u);
        await pair.Left.SendMessageAsync(0, 53, data, !ordered, timeout.Token, policy);
        await pair.Left.SendMessageAsync(0, 51, "after fragment abandonment"u8.ToArray(), !ordered, timeout.Token);
        var received = await SctpTests.Read(pair.Right, timeout.Token);
        Check(received.Data.AsSpan().SequenceEqual("after fragment abandonment"u8), "A partial message was released or later data remained blocked");
        await pair.Left.DrainAsync(timeout.Token);
        Check(pair.Left.GetDiagnostics().AbandonedMessages == 1 && pair.Right.GetDiagnostics().BufferedReceiveBytes == 0);
        await pair.Left.CloseAsync(timeout.Token);
        Check(await pair.Left.Completion == null && await pair.Right.Completion == null);
    }
    internal static async Task ForwardLoss(bool timed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast() with { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(timed ? 500 : 100) }, timeout.Token);
        pair.Proxy.DropLeftApplications = 2; // First DATA and the first FORWARD-TSN; no other left DATA exists yet.
        var policy = new SctpReliability(timed ? DataChannelReliability.Timed : DataChannelReliability.RetransmissionLimited, timed ? 250u : 0u);
        await pair.Left.SendMessageAsync(0, 53, new byte[] { 1 }, cancellationToken: timeout.Token, reliability: policy);
        while (pair.Proxy.Dropped < 2) await Task.Delay(5, timeout.Token);
        Check(pair.Left.GetDiagnostics().AbandonedMessages == 1);
        var drained = pair.Left.DrainAsync(timeout.Token); Check(!drained.IsCompleted, "Abandonment pretended the peer had acknowledged FORWARD-TSN");
        await pair.Left.SendMessageAsync(0, 51, "after lost forward"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after lost forward"u8));
        await drained;
        Check(pair.Left.GetDiagnostics().Retransmissions > 0 && pair.Proxy.Dropped == 2);
    }
    internal static async Task RetransmissionBudget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast(), timeout.Token);
        pair.Proxy.DropLeftApplications = 3;
        await pair.Left.SendMessageAsync(0, 53, new byte[] { 1 }, cancellationToken: timeout.Token,
            reliability: new(DataChannelReliability.RetransmissionLimited, 2));
        while (pair.Proxy.Dropped < 3) await Task.Delay(5, timeout.Token);
        await pair.Left.SendMessageAsync(0, 51, "after two retransmissions"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after two retransmissions"u8));
        await pair.Left.DrainAsync(timeout.Token);
        Check(pair.Left.GetDiagnostics().AbandonedMessages == 1 && pair.Left.GetDiagnostics().Retransmissions == 2 && pair.Proxy.Dropped == 3);
    }
    internal static async Task AdmissionExpiry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = SctpTests.Fast() with { MaximumQueuedMessages = 1, MaximumMessageSize = 1024, SendBufferBytes = 1024, ReceiveBufferBytes = 1500 };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        await pair.Left.SendMessageAsync(0, 53, new byte[1024], cancellationToken: timeout.Token);
        await pair.Left.DrainAsync(timeout.Token);
        await pair.Left.SendMessageAsync(0, 53, new byte[1024], cancellationToken: timeout.Token);
        await pair.Left.SendMessageAsync(0, 53, new byte[1024], cancellationToken: timeout.Token, reliability: new(DataChannelReliability.Timed, 30));
        Check(pair.Left.GetDiagnostics().AbandonedMessages == 1 && pair.Left.GetDiagnostics().BufferedSendBytes == 1024);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            try
            {
                await pair.Left.SendMessageAsync(0, 53, new byte[1024], cancellationToken: cancel.Token,
                    reliability: new(DataChannelReliability.Timed, uint.MaxValue));
                throw new InvalidOperationException("Canceled maximum-lifetime admission completed");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try
            {
                await pair.Left.SendMessageAsync(0, 53, new byte[] { 1 }, cancellationToken: canceled.Token,
                    reliability: new(DataChannelReliability.Timed, 0));
                throw new InvalidOperationException("A pre-canceled zero-lifetime send completed");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }
        Check(pair.Left.GetDiagnostics().AbandonedMessages == 1, "Cancellation was mislabeled as lifetime abandonment");
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.Length == 1024);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.Length == 1024);
        await pair.Left.DrainAsync(timeout.Token);
        await pair.Left.SendMessageAsync(0, 51, "after admission expiry"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after admission expiry"u8));
    }
    internal static async Task Negotiation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast(), timeout.Token,
            remoteOptions: SctpTests.Fast() with { EnablePartialReliability = false });
        Check(!pair.Left.SupportsPartialReliability && !pair.Right.SupportsPartialReliability);
        try
        {
            await pair.Left.SendMessageAsync(0, 51, new byte[] { 1 }, cancellationToken: timeout.Token, reliability: new(DataChannelReliability.Timed, 100));
            throw new InvalidOperationException("Unnegotiated partial reliability was silently enabled");
        }
        catch (NotSupportedException) { }
        await pair.Left.SendMessageAsync(0, 51, "reliable fallback"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("reliable fallback"u8));
        await using var channels = new DataChannelAssociation(pair.Left);
        await using var remote = new DataChannelAssociation(pair.Right);
        try
        {
            await channels.OpenChannelAsync(new("unsupported", "", true, DataChannelReliability.RetransmissionLimited, 0, 256), timeout.Token);
            throw new InvalidOperationException("Unnegotiated partially reliable DCEP was enabled");
        }
        catch (NotSupportedException) { }
        var reliable = await channels.OpenChannelAsync("available", cancellationToken: timeout.Token);
        var accepted = await DataChannelTests.Accept(remote, timeout.Token);
        Check(reliable.StreamId == 0 && accepted.StreamId == 0, "Failed policy negotiation consumed a stream ID");
    }
}
