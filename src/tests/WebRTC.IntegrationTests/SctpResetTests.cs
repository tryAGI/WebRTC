using tryAGI.WebRTC;

internal static class SctpResetTests
{
    private static void Check(bool value, string message = "Stream-reset assertion failed") => SctpTests.Check(value, message);
    private static async Task<SctpReceiveEvent> ReadEvent(SctpAssociation association, CancellationToken ct)
    { await foreach (var item in association.ReceiveEventsAsync(ct)) return item; throw new IOException("Missing SCTP event"); }
    private static void ResetEvent(SctpReceiveEvent item, bool outgoing, ushort id = 0)
    { Check(item is SctpStreamReset reset && reset.Outgoing == outgoing && reset.StreamIds.SequenceEqual(new[] { id }), "Wrong reset notification"); }
    internal static async Task Raw(bool all, bool simultaneous)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast() with { Streams = 2, InitialTransmissionSequenceNumber = uint.MaxValue }, timeout.Token);
        Check(pair.Left.SupportsStreamReset && pair.Right.SupportsStreamReset);
        // Two cycles cross reset-request sequence rollover and exercise SSN zero
        // reuse, while the association's TSNs continue monotonically.
        for (var cycle = 0; cycle < 2; cycle++)
        {
            await pair.Left.SendMessageAsync(0, 51, new byte[] { (byte)cycle }, cancellationToken: timeout.Token);
            Check(((SctpMessage)await ReadEvent(pair.Right, timeout.Token)).Data[0] == cycle);
            var left = pair.Left.ResetOutgoingStreamsAsync(all ? ReadOnlyMemory<ushort>.Empty : new ushort[] { 0 }, timeout.Token);
            if (simultaneous) await Task.WhenAll(left, pair.Right.ResetOutgoingStreamsAsync(new ushort[] { 0 }, timeout.Token));
            else await left;
            if (all)
            {
                Check(await ReadEvent(pair.Left, timeout.Token) is SctpStreamReset { Outgoing: true } local && local.StreamIds.Count == 2);
                Check(await ReadEvent(pair.Right, timeout.Token) is SctpStreamReset { Outgoing: false } remote && remote.StreamIds.Count == 2);
            }
            else if (simultaneous)
            {
                foreach (var association in new[] { pair.Left, pair.Right })
                {
                    var a = (SctpStreamReset)await ReadEvent(association, timeout.Token); var b = (SctpStreamReset)await ReadEvent(association, timeout.Token);
                    Check(a.Outgoing != b.Outgoing && a.StreamIds.Single() == 0 && b.StreamIds.Single() == 0);
                }
            }
            else { ResetEvent(await ReadEvent(pair.Left, timeout.Token), true); ResetEvent(await ReadEvent(pair.Right, timeout.Token), false); }
        }
        await pair.Left.SendMessageAsync(0, 51, "after reset"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after reset"u8));
    }
    internal static async Task Backpressure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast() with { MaximumQueuedMessages = 1 }, timeout.Token);
        await pair.Left.SendMessageAsync(0, 51, new byte[] { 1 }, cancellationToken: timeout.Token); await pair.Left.DrainAsync(timeout.Token);
        await pair.Left.SendMessageAsync(0, 51, new byte[] { 2 }, cancellationToken: timeout.Token); await pair.Left.DrainAsync(timeout.Token);
        var reset = pair.Left.ResetOutgoingStreamsAsync(new ushort[] { 0 }, timeout.Token);
        await Task.Delay(50, timeout.Token); Check(!reset.IsCompleted, "Reset skipped an earlier complete undelivered message");
        Check(((SctpMessage)await ReadEvent(pair.Right, timeout.Token)).Data[0] == 1);
        Check(((SctpMessage)await ReadEvent(pair.Right, timeout.Token)).Data[0] == 2);
        ResetEvent(await ReadEvent(pair.Right, timeout.Token), false); await reset;
        ResetEvent(await ReadEvent(pair.Left, timeout.Token), true);
        await pair.Left.SendMessageAsync(0, 51, new byte[] { 3 }, cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data[0] == 3);
    }
    internal static async Task DeferredAbandonment()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast() with { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(500) }, timeout.Token);
        pair.Proxy.DropLeftApplications = 2;
        await pair.Left.SendMessageAsync(0, 53, new byte[2500], cancellationToken: timeout.Token, reliability: new(DataChannelReliability.Timed, 250));
        while (pair.Proxy.Dropped < 2) await Task.Delay(5, timeout.Token);
        await pair.Left.ResetOutgoingStreamsAsync(new ushort[] { 0 }, timeout.Token);
        Check(pair.Left.GetDiagnostics().AbandonedMessages == 1);
        ResetEvent(await ReadEvent(pair.Right, timeout.Token), false); ResetEvent(await ReadEvent(pair.Left, timeout.Token), true);
        await pair.Left.SendMessageAsync(0, 51, "new generation"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("new generation"u8));
    }
    internal static async Task Loss(bool response)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast(), timeout.Token);
        if (response) pair.Proxy.DropRightApplications = 2; // RESET response and its accompanying SACK.
        else pair.Proxy.DropLeftApplications = 1;
        await pair.Left.ResetOutgoingStreamsAsync(new ushort[] { 0 }, timeout.Token);
        Check(pair.Proxy.Dropped >= 1 && pair.Left.GetDiagnostics().Retransmissions > 0);
        ResetEvent(await ReadEvent(pair.Right, timeout.Token), false); ResetEvent(await ReadEvent(pair.Left, timeout.Token), true);
        await pair.Left.SendMessageAsync(0, 51, "after reset loss"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after reset loss"u8));
    }
    internal static async Task Channels(bool simultaneous, bool cancellation, bool responseLoss = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast() with { Streams = 2, InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(500) }, timeout.Token);
        await using var left = new DataChannelAssociation(pair.Left, new() { MaximumQueuedMessages = 4 });
        await using var right = new DataChannelAssociation(pair.Right, new() { MaximumQueuedMessages = 4 });
        var local = await left.OpenChannelAsync("old", cancellationToken: timeout.Token); var remote = await DataChannelTests.Accept(right, timeout.Token);
        for (var i = 0; i < 3; i++) await local.SendTextAsync($"old:{i}", timeout.Token);
        await pair.Left.DrainAsync(timeout.Token); await pair.Right.DrainAsync(timeout.Token);
        if (responseLoss) { await Task.Delay(100, timeout.Token); pair.Proxy.DropRightApplications = 1; }
        if (cancellation)
        {
            pair.Proxy.DropLeftApplications = 1;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            try { await local.CloseAsync(cancel.Token); throw new InvalidOperationException("Lost reset did not outlive canceled waiter"); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Check(!local.IsOpen && !local.Completion.IsCompleted);
            try { await left.OpenChannelAsync("premature", cancellationToken: timeout.Token); throw new InvalidOperationException("Unconfirmed stream ID was reused"); }
            catch (InvalidOperationException error) when (error.Message == "No available data-channel stream.") { }
            await local.CloseAsync(timeout.Token);
        }
        else if (simultaneous) await Task.WhenAll(local.CloseAsync(timeout.Token), remote.CloseAsync(timeout.Token));
        else await local.CloseAsync(timeout.Token);
        Check(await local.Completion == null && await remote.Completion.WaitAsync(timeout.Token) == null && !local.IsOpen && !remote.IsOpen);
        if (responseLoss) Check(pair.Proxy.Dropped == 1 && pair.Left.GetDiagnostics().Retransmissions > 0, "Lost-result regression did not exercise reset retry");
        var fresh = await left.OpenChannelAsync("fresh", cancellationToken: timeout.Token); var accepted = await DataChannelTests.Accept(right, timeout.Token);
        Check(fresh.StreamId == local.StreamId && !ReferenceEquals(accepted, remote));
        await fresh.SendTextAsync("fresh generation", timeout.Token);
        Check((await DataChannelTests.Read(accepted, timeout.Token)).GetText() == "fresh generation");
        var count = 0;
        await foreach (var message in remote.ReceiveMessagesAsync(timeout.Token)) Check(message.GetText() == $"old:{count++}");
        Check(count == 3, "Closing discarded already acknowledged messages or routed them into the reused stream");
        await local.DisposeAsync(); await remote.DisposeAsync();
        await fresh.CloseAsync(timeout.Token);
    }
    internal static async Task NotificationBudget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast() with { MaximumQueuedResetEvents = 2 }, timeout.Token);
        for (var i = 0; i < 2; i++) await pair.Left.ResetOutgoingStreamsAsync(new ushort[] { 0 }, timeout.Token);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            try { await pair.Left.ResetOutgoingStreamsAsync(new ushort[] { 0 }, cancel.Token); throw new InvalidOperationException("Notification budget was unbounded"); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
        for (var i = 0; i < 2; i++) ResetEvent(await ReadEvent(pair.Left, timeout.Token), true);
        await pair.Left.SendMessageAsync(0, 51, "after notification budget"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await SctpTests.Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after notification budget"u8));
        await pair.Left.ResetOutgoingStreamsAsync(new ushort[] { 0 }, timeout.Token);
        ResetEvent(await ReadEvent(pair.Left, timeout.Token), true); ResetEvent(await ReadEvent(pair.Right, timeout.Token), false);
    }
    internal static async Task DiscardClosedBuffer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast(), timeout.Token);
        await using var left = new DataChannelAssociation(pair.Left);
        await using var right = new DataChannelAssociation(pair.Right, new() { MaximumQueuedMessages = 1, ReceiveBufferBytes = 512 });
        var local = await left.OpenChannelAsync("discard", cancellationToken: timeout.Token); var remote = await DataChannelTests.Accept(right, timeout.Token);
        await local.SendBinaryAsync(new byte[512], timeout.Token); await pair.Left.DrainAsync(timeout.Token);
        await local.CloseAsync(timeout.Token);
        await remote.DisposeAsync(); // Explicit application disposal releases old queued credit.
        var fresh = await left.OpenChannelAsync("fresh", cancellationToken: timeout.Token); var accepted = await DataChannelTests.Accept(right, timeout.Token);
        await fresh.SendTextAsync("credit returned", timeout.Token);
        Check((await DataChannelTests.Read(accepted, timeout.Token)).GetText() == "credit returned");
        await fresh.CloseAsync(timeout.Token);
    }
    internal static async Task BlockedOldSend()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = SctpTests.Fast() with { MaximumMessageSize = 16384, ReceiveBufferBytes = 16384, SendBufferBytes = 16384 };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        await using var left = new DataChannelAssociation(pair.Left);
        await using var right = new DataChannelAssociation(pair.Right, new() { MaximumQueuedMessages = 1, ReceiveBufferBytes = 512 });
        var old = await left.OpenChannelAsync("old", cancellationToken: timeout.Token); var remote = await DataChannelTests.Accept(right, timeout.Token);
        var sender = Task.Run(async () => { for (var i = 0; i < 200; i++) await old.SendBinaryAsync(new byte[512], timeout.Token); });
        await Task.Delay(100, timeout.Token); Check(!sender.IsCompleted);
        var closing = old.CloseAsync(timeout.Token);
        var drain = Task.Run(async () => { await foreach (var message in remote.ReceiveMessagesAsync(timeout.Token)) Check(message.Data.Length == 512); });
        try { await sender; throw new IOException("A blocked old-generation send survived closing"); }
        catch (InvalidOperationException error) when (error.Message is "The sending channel is closing." or "Data channel is not open.") { }
        await closing; await drain;
        var fresh = await left.OpenChannelAsync("fresh", cancellationToken: timeout.Token); var accepted = await DataChannelTests.Accept(right, timeout.Token);
        Check(fresh.StreamId == old.StreamId);
        await fresh.SendTextAsync("only fresh", timeout.Token);
        Check((await DataChannelTests.Read(accepted, timeout.Token)).GetText() == "only fresh");
        await fresh.CloseAsync(timeout.Token);
    }
    internal static async Task Negotiation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast(), timeout.Token, remoteOptions: SctpTests.Fast() with { EnableStreamReset = false });
        Check(!pair.Left.SupportsStreamReset && !pair.Right.SupportsStreamReset);
        try { await pair.Left.ResetOutgoingStreamsAsync(new ushort[] { 0 }, timeout.Token); throw new InvalidOperationException("Unnegotiated reset accepted"); }
        catch (NotSupportedException) { }
        await using var left = new DataChannelAssociation(pair.Left); await using var right = new DataChannelAssociation(pair.Right);
        var local = await left.OpenChannelAsync("still usable", cancellationToken: timeout.Token); var remote = await DataChannelTests.Accept(right, timeout.Token);
        try { await local.CloseAsync(timeout.Token); throw new InvalidOperationException("Unnegotiated DCEP close accepted"); }
        catch (NotSupportedException) { }
        await local.SendTextAsync("no state mutation", timeout.Token); Check((await DataChannelTests.Read(remote, timeout.Token)).GetText() == "no state mutation");
    }
}
