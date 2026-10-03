using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using tryAGI.WebRTC;

internal static class SrtpInterop
{
    internal static byte[] Key(SrtpProfile profile) => Enumerable.Range(1, profile == SrtpProfile.AeadAes256Gcm ? 32 : 16).Select(i => (byte)i).ToArray();
    internal static byte[] Salt(SrtpProfile profile) => Enumerable.Range(61, profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12).Select(i => (byte)i).ToArray();
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task Pion(Uri uri, SrtpProfile profile)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var rtcp in new[] { false, true })
        {
            // Synthetic public keys and independently authored test packets; no provider secrets.
            var plain = rtcp ? new[] {
                Convert.FromHexString("80c80006213243541234567812345678876543210000000400000005"),
                Convert.FromHexString("80c9000121324354"),
                Convert.FromHexString("80c900012132435480ca000100000000"),
                Convert.FromHexString("a0c900022132435400000004"),
            } : new[] {
                Convert.FromHexString("806ffffd1234567821324354010203040506070809"),
                Convert.FromHexString("b26ffffe12345678213243540000000100000002bede000110aa20bb11223304040404"),
                Convert.FromHexString("806fffff1234567821324354"),
                Convert.FromHexString("806f0000123456782132435400112233445566778899"),
                Convert.FromHexString("806f0001123456782132435444332211"),
            };
            var request = new SrtpPeerRequest((ushort)profile, Convert.ToHexString(Key(profile)), Convert.ToHexString(Salt(profile)), rtcp, false, false,
                plain.Select(Convert.ToHexString).ToArray());
            var independent = await Exchange(client, uri, request);
            using var sender = new SrtpContext(profile, SrtpDirection.Send, Key(profile), Salt(profile));
            using var receiver = new SrtpContext(profile, SrtpDirection.Receive, Key(profile), Salt(profile));
            var ourPackets = new List<string>();
            for (var i = 0; i < plain.Length; i++)
            {
                var output = new byte[plain[i].Length + sender.RtcpOverhead];
                int length;
                Check(rtcp ? sender.TryProtectRtcp(plain[i], output, out length) : sender.TryProtectRtp(plain[i], output, out length), "Protection failed");
                var encrypted = output[..length];
                Check(encrypted.AsSpan().SequenceEqual(independent[i]), $"Pion ciphertext mismatch for {profile}, RTCP={rtcp}, packet {i}");
                ourPackets.Add(Convert.ToHexString(encrypted));
            }
            // Includes reverse delivery across the rollover and RTCP indices.
            foreach (var i in new[] { 0 }.Concat(Enumerable.Range(1, plain.Length - 1).Reverse()))
            {
                var output = new byte[independent[i].Length];
                int length;
                Check(rtcp ? receiver.TryUnprotectRtcp(independent[i], output, out length) : receiver.TryUnprotectRtp(independent[i], output, out length), "Independent decryption failed");
                Check(output.AsSpan(0, length).SequenceEqual(plain[i]), "Independent plaintext mismatch");
                Check(!(rtcp ? receiver.TryUnprotectRtcp(independent[i], output, out _) : receiver.TryUnprotectRtp(independent[i], output, out _)), "Independent replay accepted");
            }
            var recovered = await Exchange(client, uri, request with { Decrypt = true, Packets = ourPackets.ToArray() });
            for (var i = 0; i < plain.Length; i++) Check(recovered[i].SequenceEqual(plain[i]), "Pion could not decrypt our ciphertext");

            if (rtcp)
            {
                var authenticationOnly = await Exchange(client, uri, request with { AuthOnly = true });
                using var controlReceiver = new SrtpContext(profile, SrtpDirection.Receive, Key(profile), Salt(profile));
                for (var i = 0; i < plain.Length; i++)
                {
                    var output = new byte[authenticationOnly[i].Length];
                    Check(controlReceiver.TryUnprotectRtcp(authenticationOnly[i], output, out var length), "Authenticated E=0 packet rejected");
                    Check(output.AsSpan(0, length).SequenceEqual(plain[i]), "E=0 plaintext mismatch");
                    Check(!controlReceiver.TryUnprotectRtcp(authenticationOnly[i], output, out _), "E=0 replay accepted");
                }
            }
        }
    }

    private static async Task<byte[][]> Exchange(HttpClient client, Uri uri, SrtpPeerRequest request)
    {
        using var response = await client.PostAsJsonAsync(new Uri(uri, "/srtp"), request, SrtpPeerJson.Default.SrtpPeerRequest);
        response.EnsureSuccessStatusCode();
        var packets = await response.Content.ReadFromJsonAsync(SrtpPeerJson.Default.StringArray) ?? throw new IOException("Missing peer result");
        Check(packets.Length == request.Packets.Length && packets.All(p => p.Length <= 8240), "Invalid peer result bounds");
        return packets.Select(Convert.FromHexString).ToArray();
    }

    internal static async Task Network(SrtpProfile profile)
    {
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], timeout.Token),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], timeout.Token));
        using var sender = new SrtpContext(profile, SrtpDirection.Send, Key(profile), Salt(profile));
        using var receiver = new SrtpContext(profile, SrtpDirection.Receive, Key(profile), Salt(profile));
        var plaintext = Convert.FromHexString("806f00011234567821324354010203040506070809");
        var encrypted = new byte[plaintext.Length + sender.RtpOverhead];
        Check(sender.TryProtectRtp(plaintext, encrypted, out _), "Network protection failed");
        await left.SendDatagramAsync(encrypted, timeout.Token);
        await foreach (var packet in right.ReceiveDatagramsAsync(timeout.Token))
        {
            var recovered = new byte[packet.Length];
            Check(receiver.TryUnprotectRtp(packet, recovered, out var length) && recovered.AsSpan(0, length).SequenceEqual(plaintext), "Network recovery failed");
            Check(!receiver.TryUnprotectRtp(packet, recovered, out _), "Network replay accepted");
            return;
        }
        throw new IOException("ICE transport ended without receiving encrypted RTP.");
    }
}

internal sealed record SrtpPeerRequest(ushort Profile, string Key, string Salt, bool Rtcp, bool Decrypt, bool AuthOnly, string[] Packets);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SrtpPeerRequest))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class SrtpPeerJson : JsonSerializerContext;
