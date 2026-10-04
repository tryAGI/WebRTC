using System.Net;
using tryAGI.WebRTC;

internal static class RtcpNetworkTests
{
    internal static async Task Network(SrtpProfile profile)
    {
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        // Public synthetic keys only; exercise all independent fields through actual UDP and authenticated SRTCP.
        var key = Enumerable.Range(1, profile == SrtpProfile.AeadAes256Gcm ? 32 : 16).Select(i => (byte)i).ToArray();
        var salt = Enumerable.Range(61, profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12).Select(i => (byte)i).ToArray();
        using var sender = new SrtpContext(profile, SrtpDirection.Send, key, salt);
        using var receiver = new SrtpContext(profile, SrtpDirection.Receive, key, salt);
        var plain = RtcpPackets.Encode(RtcpTests.Model()); var encrypted = new byte[plain.Length + sender.RtcpOverhead];
        RtcpTests.Check(sender.TryProtectRtcp(plain, encrypted, out var protectedSize) && protectedSize == encrypted.Length);
        await left.SendDatagramAsync(encrypted, ct);
        await foreach (var packet in right.ReceiveDatagramsAsync(ct))
        {
            var recovered = new byte[packet.Length];
            RtcpTests.Check(receiver.TryUnprotectRtcp(packet, recovered, out var length));
            RtcpTests.Check(recovered.AsSpan(0, length).SequenceEqual(RtcpTests.Reference()));
            RtcpTests.Check(RtcpPackets.TryParse(recovered.AsSpan(0, length), out var packets, out var compound) && compound && packets.Count == 4);
            RtcpTests.Check(((RtcpSenderReport)packets[0]).Reports[1].CumulativeLost == -2);
            RtcpTests.Check(packets[3] == new RtcpPictureLossIndication(3, 99));
            RtcpTests.Check(!receiver.TryUnprotectRtcp(packet, recovered, out _), "Encrypted replay accepted");
            return;
        }
        throw new IOException("Encrypted RTCP exchange ended without a packet.");
    }
}
