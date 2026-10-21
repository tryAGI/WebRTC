using System.Net;
using System.Net.Sockets;
using System.Text;
using tryAGI.WebRTC;

internal static class IceConsentTests
{
    // An independent ICE-lite responder holds each consent reply until a later
    // request arrives. Every valid reply is therefore for an older transaction.
    internal static async Task PreviousTransactionResponse()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var ct = deadline.Token;
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new()
        {
            CheckInterval = TimeSpan.FromMilliseconds(10),
            ConsentInterval = TimeSpan.FromMilliseconds(100),
            ConsentTimeout = TimeSpan.FromMilliseconds(900),
        });
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var remote = IceCredentials.Generate();
        var key = Encoding.ASCII.GetBytes(remote.Password);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopResponses = false;
        async Task Pump()
        {
            byte[]? previous = null;
            byte[]? lastReplied = null;
            var replies = 0;
            var buffer = new byte[2048];
            try
            {
                while (true)
                {
                    var received = await peer.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                    var response = Reply(buffer.AsSpan(0, received.ReceivedBytes), ice.LocalEndPoint, key);
                    if (response is not { } value) continue;
                    if (Volatile.Read(ref stopResponses))
                    {
                        // Replaying an already consumed authenticated response
                        // must not keep consent alive or revive an expired pair.
                        if (lastReplied is not null)
                            await peer.SendToAsync(lastReplied, SocketFlags.None, ice.LocalEndPoint, ct);
                        continue;
                    }
                    if (!value.Consent)
                        await peer.SendToAsync(value.Packet, SocketFlags.None, ice.LocalEndPoint, ct);
                    else
                    {
                        if (previous is not null)
                        {
                            await peer.SendToAsync(previous, SocketFlags.None, ice.LocalEndPoint, ct);
                            lastReplied = previous;
                            if (++replies == 15) renewed.TrySetResult();
                        }
                        previous = value.Packet;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        var pump = Pump();
        try
        {
            await ice.ConnectAsync(remote, IceRole.Controlling,
                [new(peer.LocalEndPoint as IPEndPoint ?? throw new InvalidOperationException("Unbound peer."))], ct);
            var winner = await Task.WhenAny(renewed.Task, ice.Completion).WaitAsync(ct);
            if (winner != renewed.Task) throw new InvalidOperationException("Consent expired despite authenticated replies to previous requests.");
            await renewed.Task;
            if (!ice.IsConnected || ice.GetDiagnostics().ConsentAge >= TimeSpan.FromMilliseconds(500))
                throw new InvalidOperationException("Delayed consent responses did not renew the selected pair.");
            await ice.SendDatagramAsync("still-authorized"u8.ToArray(), ct);
            Volatile.Write(ref stopResponses, true);
            if (await ice.Completion.WaitAsync(ct) is not IOException)
                throw new InvalidOperationException("Missing consent must still stop outbound traffic.");
            try
            {
                await ice.SendDatagramAsync("forbidden-after-expiry"u8.ToArray(), ct);
                throw new InvalidOperationException("Expired consent admitted application traffic.");
            }
            catch (IOException) { }
        }
        finally
        {
            await deadline.CancelAsync();
            await pump;
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    private static (byte[] Packet, bool Consent)? Reply(ReadOnlySpan<byte> packet, IPEndPoint source, byte[] key)
    {
        if (!StunMessage.TryParse(packet, out var request) || request.Type != StunMessage.BindingRequest ||
            !request.VerifyFingerprint() || !request.VerifyMessageIntegritySha1(key)) return null;
        var consent = !request.TryGetUniqueAttribute(0x0024, out _);
        var buffer = new byte[128];
        var writer = new StunMessageWriter(buffer, 0x0101, request.TransactionId);
        if (!writer.TryAddXorMappedAddress(source) || !writer.TryComplete(key, fingerprint: true, out var length))
            throw new InvalidOperationException("Consent reply exceeded bounded storage.");
        return (buffer[..length], consent);
    }
}
