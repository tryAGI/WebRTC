using System.Net;
using System.Net.Http.Json;
using tryAGI.WebRTC;

internal static class RtcpInteropTests
{
    private static void Check(bool value, string message = "Independent RTCP assertion failed") => RtcpTests.Check(value, message);
    internal static async Task Pion(Uri uri, DtlsRole role, SrtpProfile profile)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
        using var vectors = await http.GetAsync(new Uri(uri, "/rtcp-vectors"), ct); vectors.EnsureSuccessStatusCode();
        var wire = (await vectors.Content.ReadFromJsonAsync(VideoInteropJson.Default.StringArray, ct))!;
        Check(wire.Length == 4 && wire.All(s => s.Length is > 0 and <= 2400));
        var independent = wire.Select(Convert.FromHexString).ToArray();
        Check(RtcpPackets.TryParse(independent[0], out var parsed, out var compound) && compound && parsed.Count == 3);
        Check(parsed[0] is RtcpSenderReport { SenderSource: 0x21324354, NtpTimestamp: 0x123456789abcdef0, RtpTimestamp: 0x87654321, PacketCount: 4, OctetCount: 5 } sr &&
            sr.Reports.SequenceEqual(new[] { new RtcpReceptionReport(99, 64, 7, 65537, 128, 0x11223344, 65536) }));
        Check(((RtcpSourceDescription)parsed[1]).Chunks[0] == new RtcpSdesChunk(0x21324354, "peer/Иван"));
        Check(parsed[2] == new RtcpPictureLossIndication(0x21324354, 99));
        Check(RtcpPackets.Encode(parsed).SequenceEqual(independent[0]));
        Check(RtcpPackets.TryParse(independent[1], out parsed, out compound) && compound && parsed[0] is RtcpReceiverReport { SenderSource: 3, Reports.Count: 1 });
        Check(RtcpPackets.TryParse(independent[2], out parsed, out compound) && !compound && parsed[0] == new RtcpPictureLossIndication(3, 99));
        Check(RtcpPackets.TryParse(independent[3], out parsed, out compound) && !compound && parsed[0] is RtcpGoodbye { Reason: "bye/x" } bye && bye.Sources.SequenceEqual(new uint[] { 3, 4 }));
        // Pion independently decodes and re-encodes our signed report/Unicode/compound fixture too.
        var authored = RtcpPackets.Encode(RtcpTests.Model());
        using var reencoded = await http.PostAsJsonAsync(new Uri(uri, "/rtcp-reencode"), new[] { Convert.ToHexString(authored) }, VideoInteropJson.Default.StringArray, ct);
        reencoded.EnsureSuccessStatusCode(); var result = (await reencoded.Content.ReadFromJsonAsync(VideoInteropJson.Default.StringArray, ct))!;
        Check(result.Length == 1 && Convert.FromHexString(result[0]).SequenceEqual(authored));
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var endpoint = ice.LocalEndPoint;
        var offer = new DtlsOffer(true, true, role == DtlsRole.Server, Convert.ToHexString(identity.GetFingerprintSha256()), (ushort)profile, 1200,
            ice.LocalCredentials.UsernameFragment, ice.LocalCredentials.Password, $"1 1 udp 2130706431 {endpoint.Address} {endpoint.Port} typ host", independent.Length + 1);
        using var response = await http.PostAsJsonAsync(new Uri(uri, "/peer"), offer, InteropJson.Default.DtlsOffer, ct); response.EnsureSuccessStatusCode();
        var remote = (await response.Content.ReadFromJsonAsync(InteropJson.Default.DtlsDescription, ct))!;
        Check(IPAddress.TryParse(remote.Address, out var ip) && IPAddress.IsLoopback(ip));
        await ice.ConnectAsync(new(remote.Fragment, remote.Password), IceRole.Controlled, [new(new(ip!, remote.Port), remote.Priority)], ct);
        await using var secure = new DtlsSrtpTransport(ice, identity, role, Convert.FromHexString(remote.Fingerprint), new() { Profiles = [profile] });
        await secure.ConnectAsync(ct); await secure.SendApplicationDatagramAsync("rtcp-vectors"u8.ToArray(), ct);
        Check((await DtlsTests.App(secure, ct)).AsSpan().SequenceEqual("pion:rtcp-vectors"u8));
        foreach (var plain in independent.Append(authored))
        {
            await secure.SendRtcpAsync(plain, ct);
            var received = await DtlsTests.Media(secure, ct);
            Check(received.Kind == SecureMediaKind.Rtcp && received.Data.SequenceEqual(plain));
            Check(RtcpPackets.TryParse(received.Data, out _, out _));
        }
    }
}
