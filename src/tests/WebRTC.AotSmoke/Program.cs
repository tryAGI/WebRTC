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
if (!(await Read(controlled, timeout.Token)).AsSpan().SequenceEqual("native-ice"u8)) return 1;
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
    var packet = await Read(controlled, timeout.Token);
    var recovered = new byte[packet.Length];
    if (!receiver.TryUnprotectRtp(packet, recovered, out var length) || !recovered.AsSpan(0, length).SequenceEqual(data)) return 1;
    if (receiver.TryUnprotectRtp(packet, recovered, out _)) return 1;
    var control = Convert.FromHexString("80c9000100000003");
    secure = new byte[control.Length + sender.RtcpOverhead];
    if (!sender.TryProtectRtcp(control, secure, out _) || !receiver.TryUnprotectRtcp(secure, new byte[control.Length], out _)) return 1;
}
Console.WriteLine("NativeAOT SRTP/SRTCP profiles and encrypted ICE network smoke passed");
foreach (var profile in Enum.GetValues<SrtpProfile>())
{
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], deadline.Token),
        right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], deadline.Token));
    using var clientIdentity = DtlsIdentity.Generate(); using var serverIdentity = DtlsIdentity.Generate();
    var options = new DtlsSrtpOptions { Profiles = [profile], MaximumDatagramSize = 256 };
    await using var client = new DtlsSrtpTransport(left, clientIdentity, DtlsRole.Client, serverIdentity.GetFingerprintSha256(), options);
    await using var server = new DtlsSrtpTransport(right, serverIdentity, DtlsRole.Server, clientIdentity.GetFingerprintSha256(), options);
    await Task.WhenAll(client.ConnectAsync(deadline.Token), server.ConnectAsync(deadline.Token));
    await client.SendApplicationDatagramAsync("native-dtls"u8.ToArray(), deadline.Token);
    var receivedApp = false;
    await foreach (var packet in server.ReceiveApplicationDatagramsAsync(deadline.Token))
    { if (!packet.AsSpan().SequenceEqual("native-dtls"u8)) return 1; receivedApp = true; break; }
    if (!receivedApp) return 1;
    await client.SendRtpAsync(data, deadline.Token);
    var receivedMedia = false;
    await foreach (var packet in server.ReceiveMediaDatagramsAsync(deadline.Token))
    { if (packet.Kind != SecureMediaKind.Rtp || !packet.Data.AsSpan().SequenceEqual(data)) return 1; receivedMedia = true; break; }
    if (!receivedMedia) return 1;
    await using var outgoing = new SctpAssociation(client, SctpRole.Initiator, new() { MaximumPacketSize = 200 });
    await using var incoming = new SctpAssociation(server, SctpRole.Responder, new() { MaximumPacketSize = 200 });
    await Task.WhenAll(outgoing.ConnectAsync(deadline.Token), incoming.ConnectAsync(deadline.Token));
    await using var channels = new DataChannelAssociation(outgoing);
    await using var peerChannels = new DataChannelAssociation(incoming);
    var channel = await channels.OpenChannelAsync("oai-events", cancellationToken: deadline.Token);
    DataChannel? peer = null;
    await foreach (var opened in peerChannels.AcceptChannelsAsync(deadline.Token)) { peer = opened; break; }
    if (peer == null) return 1;
    var application = new byte[16384]; System.Security.Cryptography.RandomNumberGenerator.Fill(application);
    await channel.SendBinaryAsync(application, deadline.Token);
    var receivedChannel = false;
    await foreach (var message in peer.ReceiveMessagesAsync(deadline.Token))
    { if (!message.Data.AsSpan().SequenceEqual(application)) return 1; receivedChannel = true; break; }
    if (!receivedChannel) return 1;
    await outgoing.CloseAsync(deadline.Token);
}
Console.WriteLine("NativeAOT fragmented authenticated DTLS, negotiated SRTP and SCTP/DCEP channels passed");
return 0;

static async Task<byte[]> Read(IceUdpTransport transport, CancellationToken cancellationToken)
{
    await foreach (var packet in transport.ReceiveDatagramsAsync(cancellationToken)) return packet;
    throw new IOException("ICE transport ended without receiving the required smoke packet.");
}
