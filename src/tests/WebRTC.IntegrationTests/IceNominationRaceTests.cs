using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using tryAGI.WebRTC;

internal static class IceNominationRaceTests
{
    // Two authenticated candidates of one synthetic ICE-lite peer. A delayed
    // nomination acknowledgement must not start nomination of the other route.
    internal static async Task DelayedAcknowledgement(bool dropFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = deadline.Token;
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new()
        { CheckInterval = TimeSpan.FromMilliseconds(10), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100), ConnectionTimeout = TimeSpan.FromSeconds(4) });
        using var first = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var second = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0)); second.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoints = new[] { Endpoint(first), Endpoint(second) };
        var sockets = new[] { first, second };
        var remote = IceCredentials.Generate();
        var key = Encoding.ASCII.GetBytes(remote.Password);
        var nominations = new int[2];
        var selected = -1;
        async Task Pump(int index)
        {
            var buffer = new byte[2048];
            try
            {
                while (true)
                {
                    var received = await sockets[index].ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                    if (!received.RemoteEndPoint.Equals(ice.LocalEndPoint)) continue;
                    var reply = Reply(buffer.AsSpan(0, received.ReceivedBytes), ice.LocalEndPoint, key);
                    if (reply is not { } value) continue;
                    if (value.Nomination)
                    {
                        Volatile.Write(ref selected, index);
                        var count = Interlocked.Increment(ref nominations[index]);
                        if (dropFirst && count == 1) continue;
                        await Task.Delay(250, ct);
                    }
                    await sockets[index].SendToAsync(value.Packet, SocketFlags.None, ice.LocalEndPoint, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        var pumps = new[] { Pump(0), Pump(1) };
        try
        {
            await ice.ConnectAsync(remote, IceRole.Controlling,
                [new(endpoints[0], 2130706431), new(endpoints[1], 2130706430)], ct);
            if (nominations.Count(n => n > 0) != 1)
                throw new InvalidOperationException("One ICE generation nominated two pairs before the first acknowledgement.");
            var accepted = Volatile.Read(ref selected);
            if (accepted < 0 || !Equals(ice.GetDiagnostics().SelectedRemoteEndPoint, endpoints[accepted]))
                throw new InvalidOperationException("ICE-lite and controlling peer selected different routes.");
            if (dropFirst && nominations[accepted] < 2)
                throw new InvalidOperationException("Lost nomination acknowledgement did not exercise retransmission.");
            await sockets[1 - accepted].SendToAsync("unknown-source"u8.ToArray(), SocketFlags.None, ice.LocalEndPoint, ct);
            await sockets[accepted].SendToAsync("selected-source"u8.ToArray(), SocketFlags.None, ice.LocalEndPoint, ct);
            await foreach (var payload in ice.ReceiveDatagramsAsync(ct))
            {
                if (!payload.AsSpan().SequenceEqual("selected-source"u8))
                    throw new InvalidOperationException("Unselected source bypassed ICE admission.");
                break;
            }
        }
        finally
        {
            await deadline.CancelAsync();
            await Task.WhenAll(pumps);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static IPEndPoint Endpoint(Socket socket) => socket.LocalEndPoint as IPEndPoint
        ?? throw new InvalidOperationException("Loopback socket was not bound.");

    private static (byte[] Packet, bool Nomination)? Reply(ReadOnlySpan<byte> packet, IPEndPoint source, byte[] key)
    {
        if (!StunMessage.TryParse(packet, out var request) || request.Type != 1 ||
            !request.VerifyFingerprint() || !request.VerifyMessageIntegritySha1(key)) return null;
        var nominated = request.TryGetUniqueAttribute(0x0025, out _);
        var response = new byte[128];
        var writer = new StunMessageWriter(response, 0x0101, request.TransactionId);
        if (!writer.TryAddXorMappedAddress(source) || !writer.TryComplete(key, fingerprint: true, out var written))
            throw new InvalidOperationException("Synthetic STUN response exceeded its bound.");
        return (response[..written], nominated);
    }
}
