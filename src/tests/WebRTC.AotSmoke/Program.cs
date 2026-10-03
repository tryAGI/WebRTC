using tryAGI.WebRTC;

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
return 0;
