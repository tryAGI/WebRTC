using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using tryAGI.WebRTC;

internal static class VideoInteropTests
{
    private static void Check(bool result) { if (!result) throw new IOException("Independent video packetization/network assertion failed"); }
    internal static async Task Pion(Uri uri, VideoCodec codec, DtlsRole role, SrtpProfile profile)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
        // Authored synthetic encoded transport bytes, not a decoder acceptance movie or an imported sample.
        byte[] frame = codec == VideoCodec.H264 ? [ 0, 0, 0, 1, 0x67, 0x42, 0xE0, 0x1F, 0, 0, 0, 1, 0x68, 0x55, 0x66,
            0, 0, 0, 1, 0x65, .. Enumerable.Repeat((byte)0x77, 511) ] : [ 0x11, 1, 0, .. Enumerable.Repeat((byte)0x77, 509) ];
        using var packetized = await http.PostAsJsonAsync(new Uri(uri, "/video-payload"),
            new VideoPayloadRequest(codec.ToString(), Convert.ToHexString(frame), 80), VideoInteropJson.Default.VideoPayloadRequest, ct);
        packetized.EnsureSuccessStatusCode();
        var strings = (await packetized.Content.ReadFromJsonAsync(VideoInteropJson.Default.StringArray, ct))!;
        Check(strings.Length is > 3 and < 32 && strings.All(s => s.Length is > 0 and <= 160));
        var payloads = strings.Select(Convert.FromHexString).ToArray();
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var endpoint = ice.LocalEndPoint;
        var offer = new DtlsOffer(true, true, role == DtlsRole.Server, Convert.ToHexString(identity.GetFingerprintSha256()), (ushort)profile, 1200,
            ice.LocalCredentials.UsernameFragment, ice.LocalCredentials.Password, $"1 1 udp 2130706431 {endpoint.Address} {endpoint.Port} typ host",
            payloads.Length + (codec == VideoCodec.H264 ? 1 : 0));
        using var response = await http.PostAsJsonAsync(new Uri(uri, "/peer"), offer, InteropJson.Default.DtlsOffer, ct); response.EnsureSuccessStatusCode();
        var remote = (await response.Content.ReadFromJsonAsync(InteropJson.Default.DtlsDescription, ct))!;
        Check(IPAddress.TryParse(remote.Address, out var ip) && IPAddress.IsLoopback(ip));
        await ice.ConnectAsync(new(remote.Fragment, remote.Password), IceRole.Controlled, [new(new(ip!, remote.Port), remote.Priority)], ct);
        await using var secure = new DtlsSrtpTransport(ice, identity, role, Convert.FromHexString(remote.Fingerprint), new() { Profiles = [profile] });
        await secure.ConnectAsync(ct); await secure.SendApplicationDatagramAsync("video-payloads"u8.ToArray(), ct);
        Check((await DtlsTests.App(secure, ct)).AsSpan().SequenceEqual("pion:video-payloads"u8));
        using var assembler = new VideoFrameAssembler(VideoTests.Options(codec)); ushort first = 65531;
        if (codec == VideoCodec.H264)
        {
            await secure.SendRtpAsync(VideoTests.Packet((ushort)(first - 1), 1, true, [0x61, 1]), ct);
            Check(assembler.Push((await DtlsTests.Media(secure, ct)).Data).Count == 0);
        }
        var frames = new List<EncodedVideoFrame>();
        // Independent packetizer and cryptographic peer cross the RTP sequence rollover.
        var order = Enumerable.Range(0, payloads.Length);
        foreach (var i in order)
        {
            await secure.SendRtpAsync(VideoTests.Packet(unchecked((ushort)(first + i)), 90000, i == payloads.Length - 1, payloads[i]), ct);
            var packet = await DtlsTests.Media(secure, ct); Check(packet.Kind == SecureMediaKind.Rtp);
            frames.AddRange(assembler.Push(packet.Data));
        }
        Check(frames.Count == 1 && frames[0].Codec == codec && frames[0].Timestamp == 90000 && frames[0].Payload.SequenceEqual(frame));
        Check(assembler.GetDiagnostics() is { CompletedFrames: 1, BufferedFrames: 0, BufferedBytes: 0 });
    }
}
internal sealed record VideoPayloadRequest(string Codec, string Frame, ushort Mtu);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VideoPayloadRequest))]
[JsonSerializable(typeof(string[]))]
internal partial class VideoInteropJson : JsonSerializerContext;
