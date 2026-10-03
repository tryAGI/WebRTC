using tryAGI.WebRTC;
using System.Net;

Span<byte> binding = stackalloc byte[20];
if (!StunMessage.TryWriteBindingRequest(binding, "012345678901"u8) ||
    !StunMessage.TryParse(binding, out var stun) || stun.Type != StunMessage.BindingRequest)
{
    return 1;
}
var data = Convert.FromHexString("806f00010000000200000003aabb");
if (!RtpPacket.TryParse(data, out var rtp) || rtp.Payload.Length != 2)
{
    return 1;
}
Console.WriteLine("NativeAOT protocol smoke passed");
await using var controlling = new IceUdpTransport(new(IPAddress.Loopback, 0));
await using var controlled = new IceUdpTransport(new(IPAddress.Loopback, 0));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await Task.WhenAll(
    controlling.ConnectAsync(controlled.LocalCredentials, IceRole.Controlling, [new(controlled.LocalEndPoint)], timeout.Token),
    controlled.ConnectAsync(controlling.LocalCredentials, IceRole.Controlled, [new(controlling.LocalEndPoint)], timeout.Token));
await controlling.SendDatagramAsync("native-ice"u8.ToArray(), timeout.Token);
await foreach (var packet in controlled.ReceiveDatagramsAsync(timeout.Token))
{
    if (!packet.AsSpan().SequenceEqual("native-ice"u8)) return 1;
    break;
}
Console.WriteLine("NativeAOT authenticated ICE network smoke passed");
foreach (var profile in Enum.GetValues<SrtpProfile>())
{
    var key = new byte[profile == SrtpProfile.AeadAes256Gcm ? 32 : 16];
    var salt = new byte[profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12];
    System.Security.Cryptography.RandomNumberGenerator.Fill(key);
    System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
    using var sender = new SrtpContext(profile, SrtpDirection.Send, key, salt);
    using var receiver = new SrtpContext(profile, SrtpDirection.Receive, key, salt);
    System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
    System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);
    var secure = new byte[data.Length + sender.RtpOverhead];
    if (!sender.TryProtectRtp(data, secure, out _)) return 1;
    await controlling.SendDatagramAsync(secure, timeout.Token);
    await foreach (var packet in controlled.ReceiveDatagramsAsync(timeout.Token))
    {
        var recovered = new byte[packet.Length];
        if (!receiver.TryUnprotectRtp(packet, recovered, out var length) || !recovered.AsSpan(0, length).SequenceEqual(data)) return 1;
        if (receiver.TryUnprotectRtp(packet, recovered, out _)) return 1;
        break;
    }
    var control = Convert.FromHexString("80c9000100000003");
    secure = new byte[control.Length + sender.RtcpOverhead];
    if (!sender.TryProtectRtcp(control, secure, out _) || !receiver.TryUnprotectRtcp(secure, new byte[control.Length], out _)) return 1;
}
Console.WriteLine("NativeAOT SRTP/SRTCP profiles and encrypted ICE network smoke passed");
return 0;
