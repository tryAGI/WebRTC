using System.Net;
using tryAGI.WebRTC;

internal static class IdleMaintenanceTests
{
    internal static async Task SelectedSctpWake()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await using var left = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) });
        await using var right = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) });
        var offer = left.CreateOffer(); var answer = right.CreateAnswer(offer); left.SetRemoteAnswer(answer);
        await Task.WhenAll(left.ConnectAsync(ct), right.ConnectAsync(ct));
        for (var cycle = 0; cycle < 2; cycle++)
        {
            await using var channel = await left.OpenDataChannelAsync(new("idle-wake", "", true, DataChannelReliability.Reliable, 0, 8192), ct);
            await using var incoming = right.AcceptDataChannelsAsync(ct).GetAsyncEnumerator(ct);
            if (!await incoming.MoveNextAsync()) throw new IOException("Idle channel was not accepted");
            var accepted = incoming.Current;
            await Task.Delay(100, ct);
            var before = left.GetDiagnostics().Sctp ?? throw new IOException("Missing SCTP counters");
            await Task.Delay(150, ct);
            var after = left.GetDiagnostics().Sctp ?? throw new IOException("Missing SCTP counters");
            if (before.OutstandingChunks != 0 || after.MaintenanceTicks != before.MaintenanceTicks)
                throw new IOException("Quiescent SCTP continued polling");
            await using var messages = accepted.ReceiveMessagesAsync(ct).GetAsyncEnumerator(ct);
            await channel.SendBinaryAsync("wake-after-idle"u8.ToArray(), ct);
            if (!await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct) ||
                !messages.Current.Data.AsSpan().SequenceEqual("wake-after-idle"u8))
                throw new IOException("Idle SCTP delayed or corrupted data");
            await channel.CloseAsync(ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        await Task.WhenAll(left.DisposeAsync().AsTask(), right.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2), ct);
    }

    internal static async Task SelectedDisposal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await Task.WhenAll(
            left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        // The selected-state checker is now asleep until its five-second consent
        // deadline. Receiving and shutdown must remain event driven.
        await Task.Delay(100, ct);
        await left.SendDatagramAsync("idle-wakeup"u8.ToArray(), ct);
        await using var receive = right.ReceiveDatagramsAsync(ct).GetAsyncEnumerator(ct);
        if (!await receive.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct) ||
            !receive.Current.AsSpan().SequenceEqual("idle-wakeup"u8))
            throw new IOException("Idle consent scheduling delayed incoming data.");
        await Task.WhenAll(left.DisposeAsync().AsTask(), right.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(2), ct);
    }
}
