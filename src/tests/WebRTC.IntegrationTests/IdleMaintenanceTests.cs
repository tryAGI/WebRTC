using System.Net;
using tryAGI.WebRTC;

internal static class IdleMaintenanceTests
{
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
