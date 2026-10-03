using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using tryAGI.WebRTC;

internal static class DataChannelTests
{
    private static void Check(bool value, string message = "Data-channel assertion failed") => SctpTests.Check(value, message);
    internal static async Task<DataChannel> Accept(DataChannelAssociation channels, CancellationToken ct)
    { await foreach (var channel in channels.AcceptChannelsAsync(ct)) return channel; throw new IOException("Required data channel missing"); }
    internal static async Task<DataChannelMessage> Read(DataChannel channel, CancellationToken ct)
    { await foreach (var message in channel.ReceiveMessagesAsync(ct)) return message; throw new IOException("Required data-channel message missing"); }
    internal static async Task Exchange(bool ordered, DataChannelReliability reliability = DataChannelReliability.Reliable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pair = await SctpPair.Create(SctpTests.Fast(), timeout.Token);
        await using var left = new DataChannelAssociation(pair.Left); await using var right = new DataChannelAssociation(pair.Right);
        var local = await left.OpenChannelAsync(new("oai-events", "json", ordered, reliability,
            reliability == DataChannelReliability.Timed ? 3000u : reliability == DataChannelReliability.RetransmissionLimited ? 2u : 0u, 256), timeout.Token);
        var remote = await Accept(right, timeout.Token);
        Check((local.StreamId & 1) == 0 && remote.StreamId == local.StreamId);
        Check(remote.Parameters.Label == "oai-events" && remote.Parameters.Protocol == "json" && remote.Parameters.Ordered == ordered);
        Check(remote.Parameters.Reliability == reliability && remote.Parameters.ReliabilityParameter == local.Parameters.ReliabilityParameter);
        await local.SendTextAsync("{\"text\":\"Привет\"}", timeout.Token);
        Check((await Read(remote, timeout.Token)).GetText() == "{\"text\":\"Привет\"}");
        var binary = new byte[262144]; RandomNumberGenerator.Fill(binary);
        await remote.SendBinaryAsync(binary, timeout.Token);
        var received = await Read(local, timeout.Token); Check(received.Kind == DataChannelMessageKind.Binary && received.Data.AsSpan().SequenceEqual(binary));
        await local.SendTextAsync("", timeout.Token); Check((await Read(remote, timeout.Token)).GetText() == "");
        await local.SendBinaryAsync(Array.Empty<byte>(), timeout.Token); received = await Read(remote, timeout.Token);
        Check(received.Kind == DataChannelMessageKind.Binary && received.Data.Length == 0);
        var second = await right.OpenChannelAsync("second", cancellationToken: timeout.Token);
        var accepted = await Accept(left, timeout.Token); Check((second.StreamId & 1) == 1 && second.StreamId == accepted.StreamId);
        await second.SendTextAsync("independent stream", timeout.Token);
        Check((await Read(accepted, timeout.Token)).GetText() == "independent stream");
        await pair.Left.DrainAsync(timeout.Token); await pair.Right.DrainAsync(timeout.Token);
    }
    internal static async Task Backpressure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = SctpTests.Fast() with { MaximumMessageSize = 16384, ReceiveBufferBytes = 16384, SendBufferBytes = 16384 };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        await using var left = new DataChannelAssociation(pair.Left);
        await using var right = new DataChannelAssociation(pair.Right, new() { MaximumQueuedMessages = 2, ReceiveBufferBytes = 1024 });
        var local = await left.OpenChannelAsync("bounded", cancellationToken: timeout.Token);
        var remote = await Accept(right, timeout.Token);
        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++) { var bytes = new byte[512]; bytes[0] = (byte)i; await local.SendBinaryAsync(bytes, timeout.Token); }
        });
        await Task.Delay(100, timeout.Token); Check(!producer.IsCompleted, "Reliable channel failed to apply bounded backpressure");
        for (var i = 0; i < 100; i++)
        {
            try { Check((await Read(remote, timeout.Token)).Data[0] == i, "Backpressure lost or reordered reliable data"); }
            catch (Exception error) { throw new IOException($"Backpressure stalled at message {i}; send={pair.Left.GetDiagnostics()}; receive={pair.Right.GetDiagnostics()}", error); }
        }
        await producer; await pair.Left.DrainAsync(timeout.Token);
    }
    internal static async Task CancelBlockedSend()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = SctpTests.Fast() with { MaximumMessageSize = 16384, ReceiveBufferBytes = 16384, SendBufferBytes = 16384 };
        await using var pair = await SctpPair.Create(options, timeout.Token);
        await using var left = new DataChannelAssociation(pair.Left);
        await using var right = new DataChannelAssociation(pair.Right, new() { MaximumQueuedMessages = 1, ReceiveBufferBytes = 512 });
        var channel = await left.OpenChannelAsync("cancel-blocked", cancellationToken: timeout.Token);
        await Accept(right, timeout.Token);
        var producer = Task.Run(async () => { for (var i = 0; i < 200; i++) await channel.SendBinaryAsync(new byte[512], timeout.Token); });
        await Task.Delay(100, timeout.Token); Check(!producer.IsCompleted);
        await left.DisposeAsync();
        try { await producer; throw new InvalidOperationException("Disposed owner left a blocked send active"); }
        catch (OperationCanceledException) { }
        Check(pair.Left.IsConnected && pair.ClientDtls.IsConnected);
    }
    internal static async Task Pion(Uri uri, DtlsRole dtlsRole, bool peerOpens, bool unordered, bool bothInitiate = false,
        DataChannelReliability reliability = DataChannelReliability.Reliable, string scenario = "")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var endpoint = ice.LocalEndPoint;
        var offer = new ChannelOffer(true, true, dtlsRole == DtlsRole.Server, Convert.ToHexString(identity.GetFingerprintSha256()),
            7, 1200, ice.LocalCredentials.UsernameFragment, ice.LocalCredentials.Password,
            $"1 1 udp 2130706431 {endpoint.Address} {endpoint.Port} typ host", true, dtlsRole == DtlsRole.Server || bothInitiate, peerOpens, unordered,
            (int)reliability, scenario.Length != 0 ? 0u : reliability == DataChannelReliability.Timed ? 3000u : 2u, scenario);
        using var response = await http.PostAsJsonAsync(new Uri(uri, "/peer"), offer, InteropJson.Default.ChannelOffer, timeout.Token);
        response.EnsureSuccessStatusCode();
        var remote = await response.Content.ReadFromJsonAsync(InteropJson.Default.DtlsDescription, timeout.Token) ?? throw new IOException("Missing independent peer");
        Check(IPAddress.TryParse(remote.Address, out var ip) && IPAddress.IsLoopback(ip));
        await ice.ConnectAsync(new(remote.Fragment, remote.Password), IceRole.Controlled, [new(new(ip!, remote.Port), remote.Priority)], timeout.Token);
        await using var dtls = new DtlsSrtpTransport(ice, identity, dtlsRole, Convert.FromHexString(remote.Fingerprint));
        await dtls.ConnectAsync(timeout.Token);
        await using var association = new SctpAssociation(dtls, dtlsRole == DtlsRole.Client ? SctpRole.Initiator : SctpRole.Responder, SctpTests.Fast());
        await association.ConnectAsync(timeout.Token);
        await using var channels = new DataChannelAssociation(association);
        var channel = peerOpens ? await Accept(channels, timeout.Token) : await channels.OpenChannelAsync(
            new("oai-events", "json", !unordered, reliability, offer.ReliabilityParameter, 256), timeout.Token);
        Check(channel.Parameters.Label == "oai-events" && channel.Parameters.Ordered == !unordered);
        Check(channel.Parameters.Reliability == reliability);
        if (scenario == "incoming-loss")
        {
            var lost = new byte[8192]; Array.Fill(lost, (byte)0xaa);
            await channel.SendBinaryAsync(lost, timeout.Token); await channel.SendTextAsync("after-skip", timeout.Token);
            Check((await Read(channel, timeout.Token)).GetText() == "after-skip");
            Check(association.GetDiagnostics().AbandonedMessages == 1, "Independent receiver did not require our complete message abandonment");
            Check(await association.Completion.WaitAsync(timeout.Token) == null); return;
        }
        if (scenario is "peer-loss" or "malformed-forward")
        {
            try { Check((await Read(channel, timeout.Token)).GetText() == "after-skip", "Independent FORWARD-TSN left data blocked or leaked a partial message"); }
            catch (OperationCanceledException error) { throw new IOException($"Independent PR-SCTP receive stalled: {association.GetDiagnostics()}", error); }
            if (scenario == "malformed-forward") Check(association.GetDiagnostics().RejectedPackets >= 2, "Malformed FORWARD-TSN changed receive state or was accepted");
            await channel.SendTextAsync("after-skip", timeout.Token);
            Check(await association.Completion.WaitAsync(timeout.Token) == null); return;
        }
        if (peerOpens) Check((await Read(channel, timeout.Token)).GetText() == "pion:ready");
        await channel.SendTextAsync("{\"type\":\"session.update\"}", timeout.Token);
        Check((await Read(channel, timeout.Token)).GetText() == "{\"type\":\"session.update\"}");
        var bytes = new byte[262144]; RandomNumberGenerator.Fill(bytes);
        await channel.SendBinaryAsync(bytes, timeout.Token); var received = await Read(channel, timeout.Token);
        Check(received.Kind == DataChannelMessageKind.Binary && received.Data.AsSpan().SequenceEqual(bytes));
        await channel.SendTextAsync("", timeout.Token); Check((await Read(channel, timeout.Token)).GetText() == "");
        await channel.SendBinaryAsync(Array.Empty<byte>(), timeout.Token); received = await Read(channel, timeout.Token);
        Check(received.Kind == DataChannelMessageKind.Binary && received.Data.Length == 0);
        Check(await association.Completion.WaitAsync(timeout.Token) == null, "Independent SCTP shutdown did not complete cleanly");
    }
}
internal sealed record ChannelOffer(bool Controlling, bool Secure, bool DtlsClient, string Fingerprint, ushort Profile, int Mtu,
    string Fragment, string Password, string Candidate, bool DataChannels, bool SctpClient, bool PeerOpensChannels, bool Unordered,
    int Reliability = 0, uint ReliabilityParameter = 0, string ExtensionScenario = "");
