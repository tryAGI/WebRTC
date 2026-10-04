using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using tryAGI.WebRTC;

internal static class SctpTests
{
    internal static SctpOptions Fast() => new() { InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100), HandshakeTimeout = TimeSpan.FromSeconds(4) };
    internal static void Check(bool value, string message = "SCTP assertion failed") { if (!value) throw new InvalidOperationException(message); }
    internal static async Task<SctpMessage> Read(SctpAssociation association, CancellationToken ct)
    { await foreach (var message in association.ReceiveMessagesAsync(ct)) return message; throw new IOException("Required SCTP message missing"); }
    internal static async Task Exchange(bool unordered, bool wrap = false, int drops = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = Fast() with { InitialTransmissionSequenceNumber = wrap ? uint.MaxValue - 4 : null };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        pair.Proxy.DropLeftApplications = drops;
        var bytes = new byte[256 * 1024]; RandomNumberGenerator.Fill(bytes);
        await pair.Left.SendMessageAsync(3, 53, bytes, unordered, timeout.Token);
        var received = await Read(pair.Right, timeout.Token);
        Check(received.StreamId == 3 && received.PayloadProtocolIdentifier == 53 && received.Unordered == unordered && received.Data.AsSpan().SequenceEqual(bytes));
        await pair.Left.DrainAsync(timeout.Token);
        await pair.Right.SendMessageAsync(5, 51, "opposite direction"u8.ToArray(), unordered, timeout.Token);
        received = await Read(pair.Left, timeout.Token);
        Check(received.Data.AsSpan().SequenceEqual("opposite direction"u8));
        await pair.Right.DrainAsync(timeout.Token);
        if (drops != 0) Check(pair.Proxy.Dropped == drops && pair.Left.GetDiagnostics().Retransmissions > 0);
        await pair.Left.CloseAsync(timeout.Token);
        Check(await pair.Left.Completion == null && await pair.Right.Completion == null);
        Check(pair.ClientDtls.IsConnected && pair.ServerDtls.IsConnected, "SCTP shutdown disposed its owner's DTLS");
    }
    internal static async Task HandshakeLoss()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(Fast(), timeout.Token, initialDrops: 2);
        Check(pair.Proxy.Dropped == 2 && pair.Left.GetDiagnostics().Retransmissions >= 2);
        await pair.Left.SendMessageAsync(0, 51, "after init loss"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after init loss"u8));
    }
    internal static async Task Backpressure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = Fast() with { MaximumQueuedMessages = 1, MaximumMessageSize = 16384, ReceiveBufferBytes = 16384, SendBufferBytes = 16384 };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        var first = new byte[16384]; Array.Fill(first, (byte)1);
        var second = new byte[16384]; Array.Fill(second, (byte)2);
        await pair.Left.SendMessageAsync(0, 53, first, cancellationToken: timeout.Token);
        await pair.Left.DrainAsync(timeout.Token);
        await pair.Left.SendMessageAsync(0, 53, second, cancellationToken: timeout.Token);
        var third = pair.Left.SendMessageAsync(0, 53, second, cancellationToken: timeout.Token).AsTask();
        await Task.Delay(50, timeout.Token);
        Check(!third.IsCompleted, "Bounded send admission did not apply backpressure");
        Check(pair.Right.GetDiagnostics().BufferedReceiveBytes <= options.ReceiveBufferBytes);
        Check((await Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual(first));
        Check((await Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual(second));
        await third;
        Check((await Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual(second));
        await pair.Left.DrainAsync(timeout.Token);
        Check(pair.Left.GetDiagnostics().BufferedSendBytes == 0 && pair.Right.GetDiagnostics().BufferedReceiveBytes == 0);
    }
    internal static async Task ShutdownWithBufferedMessages()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(Fast() with { MaximumQueuedMessages = 1 }, timeout.Token);
        for (var i = 0; i < 3; i++)
        {
            await pair.Left.SendMessageAsync(0, 53, new byte[] { (byte)i }, cancellationToken: timeout.Token);
            await pair.Left.DrainAsync(timeout.Token);
        }
        await pair.Left.CloseAsync(timeout.Token);
        Check(await pair.Right.Completion == null);
        for (var i = 0; i < 3; i++) Check((await Read(pair.Right, timeout.Token)).Data[0] == i, "Shutdown discarded acknowledged queued data");
        var extra = false; await foreach (var message in pair.Right.ReceiveMessagesAsync(timeout.Token)) extra = true;
        Check(!extra);
    }
    internal static async Task TransportClosureIsFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(Fast(), timeout.Token);
        var reader = Read(pair.Left, timeout.Token);
        // Stop the lower owner without a SCTP terminal exchange. Pending input
        // and canceled output must not turn this into graceful SCTP completion.
        await pair.ClientDtls.DisposeAsync();
        Check(await pair.Left.Completion.WaitAsync(timeout.Token) != null,
            "Abrupt DTLS disposal masqueraded as graceful SCTP shutdown");
        try { await reader; throw new InvalidOperationException("Abrupt shutdown delivered a message"); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
        Check(pair.Right.IsConnected, "Stopping one DTLS owner changed the other local SCTP owner");
    }
    internal static async Task LossWithOneDeliverySlot(bool fullByteBudget = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = Fast() with { MaximumQueuedMessages = 1 };
        if (fullByteBudget) options = options with { MaximumMessageSize = 1500, ReceiveBufferBytes = 1500 };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        pair.Proxy.DropLeftApplications = 1;
        for (var i = 0; i < 3; i++)
        {
            var data = new byte[fullByteBudget ? 1500 : 1]; Array.Fill(data, (byte)i);
            await pair.Left.SendMessageAsync(0, 53, data, cancellationToken: timeout.Token);
        }
        await Task.Delay(50, timeout.Token);
        for (var i = 0; i < 3; i++) Check((await Read(pair.Right, timeout.Token)).Data[0] == i, "Ordered gap filling deadlocked the delivery bound");
        await pair.Left.DrainAsync(timeout.Token);
        Check(pair.Proxy.Dropped == 1 && pair.Right.GetDiagnostics().BufferedReceiveBytes == 0);
    }
    internal static async Task CanceledOrSilent(bool canceled)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = Fast() with { HandshakeTimeout = TimeSpan.FromMilliseconds(150) };
        await using var pair = await SctpPair.Create(options, timeout.Token, connectSctp: false);
        using var cancel = new CancellationTokenSource(canceled ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(3));
        try { await pair.Left.ConnectAsync(cancel.Token); throw new InvalidOperationException("Silent SCTP peer connected"); }
        catch (OperationCanceledException) when (canceled) { }
        catch (TimeoutException) when (!canceled) { }
        var reason = await pair.Left.Completion;
        Check(canceled ? reason is OperationCanceledException : reason is TimeoutException);
        Check(pair.ClientDtls.IsConnected && pair.ServerDtls.IsConnected);
    }
    internal static async Task Malformed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var pair = await SctpPair.Create(Fast(), timeout.Token);
        for (var i = 0; i < 32; i++) await pair.ClientDtls.SendApplicationDatagramAsync(new byte[32], timeout.Token);
        while (pair.Right.GetDiagnostics().RejectedPackets < 32) await Task.Delay(5, timeout.Token);
        await pair.Left.SendMessageAsync(1, 53, "after corruption"u8.ToArray(), cancellationToken: timeout.Token);
        Check((await Read(pair.Right, timeout.Token)).Data.AsSpan().SequenceEqual("after corruption"u8));
    }
}
