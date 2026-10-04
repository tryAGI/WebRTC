using tryAGI.WebRTC;

// Authored from RFC 8831/8832 reset and early-data procedures; no peer implementation imports.
internal static class DataChannelGenerationTests
{
    internal static async Task EarlyOpen(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(new SctpOptions
        { Streams = 2, InitialRetransmissionTimeout = TimeSpan.FromSeconds(1), HandshakeTimeout = TimeSpan.FromSeconds(4) }, timeout.Token);
        await using var owner = new DataChannelAssociation(pair.Left, new()
        { MaximumChannels = 1, MaximumQueuedMessages = 2, ReceiveBufferBytes = 32 });
        const ushort id = 1; // Raw right peer is the DTLS server.
        var parameters = new DataChannelParameters("old", "", true, DataChannelReliability.Reliable, 0, 256);
        await pair.Right.SendMessageAsync(id, 50, DataChannelProtocol.EncodeOpen(parameters), cancellationToken: timeout.Token);
        Check(DataChannelProtocol.IsAcknowledgment((await Read(pair.Right, timeout.Token)).Data));
        var old = await Accept(owner, timeout.Token);
        await pair.Right.SendMessageAsync(id, 51, "old"u8.ToArray(), cancellationToken: timeout.Token);
        await pair.Right.DrainAsync(timeout.Token);
        await pair.Left.DrainAsync(timeout.Token);
        // Leave the old message unread to check generation isolation and shared receive credit.
        await Task.Delay(50, timeout.Token);
        pair.Proxy.DropRightApplications = 1;
        var closing = old.CloseAsync(timeout.Token);
        Check(await ReadEvent(pair.Right, timeout.Token) is SctpStreamReset { Outgoing: false });
        while (pair.Proxy.Dropped != 1) await Task.Delay(1, timeout.Token);
        if (scenario != "unconfirmed-incoming")
        {
            await pair.Right.ResetOutgoingStreamsAsync(new ushort[] { id }, timeout.Token);
            Check(await ReadEvent(pair.Right, timeout.Token) is SctpStreamReset { Outgoing: true });
        }
        var freshOpen = DataChannelProtocol.EncodeOpen(parameters with { Label = "fresh" });
        if (scenario == "malformed") freshOpen[1] = 0xff;
        await pair.Right.SendMessageAsync(id, 50, freshOpen, cancellationToken: timeout.Token);
        if (scenario == "further-reset")
            await pair.Right.ResetOutgoingStreamsAsync(new ushort[] { id }, timeout.Token);
        else if (scenario == "duplicate")
            await pair.Right.SendMessageAsync(id, 50, freshOpen, cancellationToken: timeout.Token);
        else if (scenario == "bytes")
            await pair.Right.SendMessageAsync(id, 53, new byte[30], cancellationToken: timeout.Token);
        else if (scenario is "messages" or "valid")
        {
            await pair.Right.SendMessageAsync(id, 51, "early"u8.ToArray(), cancellationToken: timeout.Token);
            if (scenario == "messages") await pair.Right.SendMessageAsync(id, 56, new byte[] { 0 }, cancellationToken: timeout.Token);
        }
        if (scenario != "valid")
        {
            var failure = await owner.Completion.WaitAsync(timeout.Token);
            Check(failure is IOException, $"Unsafe next generation was admitted: {scenario}, {failure}");
            try { await closing; throw new InvalidOperationException("Unsafe generation closed gracefully"); }
            catch (IOException) { }
            return;
        }
        await pair.Right.DrainAsync(timeout.Token);
        await Task.Delay(100, timeout.Token);
        Check(!closing.IsCompleted && !old.Completion.IsCompleted, "Lost outgoing result prematurely released the old generation");
        var accepted = Accept(owner, timeout.Token);
        Check(!accepted.IsCompleted, "Pending generation was publicly admitted before reset confirmation");
        await closing;
        var fresh = await accepted;
        Check(fresh.Parameters.Label == "fresh" && fresh.IsOpen && !ReferenceEquals(fresh, old));
        Check((await Read(fresh, timeout.Token)).GetText() == "early", "Early new data was lost or reached the old channel");
        Check((await Read(old, timeout.Token)).GetText() == "old", "Old unread data did not survive reuse");
        Check(pair.Left.GetDiagnostics().Retransmissions > 0, "Lost result did not force explicit reset retry");
        Check(DataChannelProtocol.IsAcknowledgment((await Read(pair.Right, timeout.Token)).Data));
        await fresh.SendTextAsync("new reply", timeout.Token);
        Check((await Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("new reply"u8));
    }
    private static void Check(bool value, string message = "Data-channel generation assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task<SctpMessage> Read(SctpAssociation association, CancellationToken ct)
    { await foreach (var message in association.ReceiveMessagesAsync(ct)) return message; throw new IOException("Missing SCTP generation message"); }
    private static async Task<DataChannelMessage> Read(DataChannel channel, CancellationToken ct)
    { await foreach (var message in channel.ReceiveMessagesAsync(ct)) return message; throw new IOException("Missing channel generation message"); }
    private static async Task<DataChannel> Accept(DataChannelAssociation association, CancellationToken ct)
    { await foreach (var channel in association.AcceptChannelsAsync(ct)) return channel; throw new IOException("Missing accepted generation"); }
    private static async Task<SctpReceiveEvent> ReadEvent(SctpAssociation association, CancellationToken ct)
    {
        await foreach (var item in association.ReceiveEventsAsync(ct)) return item;
        throw new IOException("Missing SCTP generation event");
    }
}
