using tryAGI.WebRTC;

internal static class DataChannelAckTests
{
    internal static async Task Exchange(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var pair = await SctpPair.Create(new() { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100) }, ct);
        await using var channels = new DataChannelAssociation(pair.Left);
        var ordered = scenario is "ordered-reject" or "canonical-ordered";
        var opening = channels.OpenChannelAsync(new("owned", "local", ordered, DataChannelReliability.Reliable, 0, 256), ct);
        SctpMessage? open = null;
        await foreach (var message in pair.Right.ReceiveMessagesAsync(ct)) { open = message; break; }
        if (open == null || open.PayloadProtocolIdentifier != 50 || open.Unordered || !DataChannelProtocol.TryParseOpen(open.Data, out var parameters) || parameters!.Ordered != ordered)
            throw new IOException("Fixture did not receive the local OPEN.");
        var success = scenario is "canonical-unordered" or "canonical-ordered" or "padded-ordered";
        var unorderedAck = scenario is not ("canonical-ordered" or "padded-ordered");
        var ack = scenario is "padded-reject" or "padded-ordered" ? new byte[] { 2, 0, 0, 0 } : new byte[] { 2 };
        var stream = open.StreamId;
        if (scenario == "unknown-reject") stream = 2;
        if (scenario == "remote-reject")
        {
            await pair.Right.SendMessageAsync(1, 50, DataChannelProtocol.EncodeOpen(new("remote", "local", false, DataChannelReliability.Reliable, 0, 256)), cancellationToken: ct);
            await foreach (var channel in channels.AcceptChannelsAsync(ct)) { if (channel.StreamId != 1) throw new IOException("Unexpected incoming stream."); break; }
            stream = 1;
        }
        if (scenario == "open-reject") ack = DataChannelProtocol.EncodeOpen(new("invalid", "local", false, DataChannelReliability.Reliable, 0, 256));
        await pair.Right.SendMessageAsync(stream, 50, ack, unorderedAck, ct);
        if (success)
        {
            var channel = await opening;
            if (!channel.IsOpen || channel.Parameters.Ordered != ordered) throw new IOException("Acknowledged channel did not open.");
            await channel.SendTextAsync("after-ack", ct);
            var found = false;
            await foreach (var message in pair.Right.ReceiveMessagesAsync(ct))
            {
                if (message.PayloadProtocolIdentifier == 50) continue;
                if (message.StreamId != stream || message.Unordered != !ordered || !message.Data.AsSpan().SequenceEqual("after-ack"u8))
                    throw new IOException("ACK exception changed application routing or ordering.");
                found = true; break;
            }
            if (!found) throw new IOException("No application message after ACK.");
        }
        else
        {
            if (await channels.Completion.WaitAsync(ct) is not IOException) throw new IOException("Invalid DCEP control was admitted.");
            try { await opening; throw new InvalidOperationException("Invalid ACK opened a channel."); }
            catch (IOException) { }
        }
    }
}
