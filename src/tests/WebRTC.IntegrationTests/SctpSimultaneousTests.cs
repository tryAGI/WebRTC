using tryAGI.WebRTC;

internal static class SctpSimultaneousTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new IOException(message);
    }
    private static async Task<T> First<T>(IAsyncEnumerable<T> source, CancellationToken ct)
    {
        await foreach (var item in source.WithCancellation(ct)) return item;
        throw new IOException("Expected channel/message was not delivered.");
    }

    internal static async Task InitialLoss(bool both)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = deadline.Token;
        await using var pair = await SctpPair.Create(new()
        {
            HandshakeTimeout = TimeSpan.FromSeconds(5),
            InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100)
        }, ct, connectSctp: false, rightRole: SctpRole.Initiator);
        // ICE/DTLS are already established. The first application record from each
        // endpoint is its SCTP INIT; no SCTP/DCEP data has been sent yet.
        pair.Proxy.DropLeftApplications = 1;
        pair.Proxy.DropRightApplications = both ? 1 : 0;
        await Task.WhenAll(pair.Left.ConnectAsync(ct), pair.Right.ConnectAsync(ct));
        Check(pair.Left.Role == SctpRole.Initiator && pair.Right.Role == SctpRole.Initiator &&
            pair.Left.IsConnected && pair.Right.IsConnected, "Both initiators must establish despite INIT loss.");
        Check(pair.Proxy.Dropped == (both ? 2 : 1), "Required INIT loss was not exercised.");
        if (both)
            Check(pair.Left.GetDiagnostics().Retransmissions + pair.Right.GetDiagnostics().Retransmissions > 0,
                "Dropping both initial INIT records must exercise a handshake retry timer.");

        await using var left = new DataChannelAssociation(pair.Left);
        await using var right = new DataChannelAssociation(pair.Right);
        var accepting = First(right.AcceptChannelsAsync(ct), ct);
        var local = await left.OpenChannelAsync(new("loss-control", "", true,
            DataChannelReliability.Reliable, 0, 256), ct);
        var remote = await accepting;
        Check(local.StreamId % 2 == 0, "SCTP initiator collision must preserve DTLS-client stream parity.");
        var received = First(remote.ReceiveMessagesAsync(ct), ct);
        await local.SendTextAsync("after-lost-init", ct);
        Check((await received).GetText() == "after-lost-init", "Remote payload mismatch after INIT recovery.");
        received = First(local.ReceiveMessagesAsync(ct), ct);
        await remote.SendTextAsync("recovered", ct);
        Check((await received).GetText() == "recovered", "Owned payload mismatch after INIT recovery.");
        Console.WriteLine($"Simultaneous SCTP INIT loss recovered: dropped={pair.Proxy.Dropped}, " +
            $"leftRetries={pair.Left.GetDiagnostics().Retransmissions}, rightRetries={pair.Right.GetDiagnostics().Retransmissions}.");
    }
}
