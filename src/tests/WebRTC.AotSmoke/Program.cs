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
return 0;
