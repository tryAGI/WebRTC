using System.Diagnostics;
using System.Net;
using tryAGI.WebRTC;

internal static class ChannelBenchmarks
{
    internal static async Task Run()
    {
        Console.WriteLine("payload_bytes,sample,messages,process_managed_bytes_per_message,ns_per_message");
        foreach (var length in new[] { 60, 4096 }) await Scenario(length);
    }
    private static async Task Scenario(int length)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        await using var left = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) });
        await using var right = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) });
        var offer = left.CreateOffer(); var answer = right.CreateAnswer(offer); left.SetRemoteAnswer(answer);
        await Task.WhenAll(left.ConnectAsync(ct), right.ConnectAsync(ct));
        await using var channel = await left.OpenDataChannelAsync(new("benchmark", "", true, DataChannelReliability.Reliable, 0, 8192), ct);
        await using var incoming = right.AcceptDataChannelsAsync(ct).GetAsyncEnumerator(ct);
        if (!await incoming.MoveNextAsync()) throw new IOException("No benchmark channel accepted");
        var accepted = incoming.Current;
        await using var reader = accepted.ReceiveMessagesAsync(ct).GetAsyncEnumerator(ct);
        var payload = new byte[length]; payload[0] = 42;
        await Exercise(64);
        for (var sample = 0; sample < 5; sample++)
        {
            const int count = 256;
            var before = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
            await Exercise(count);
            var elapsed = Stopwatch.GetElapsedTime(start); var bytes = GC.GetTotalAllocatedBytes(true) - before;
            Console.WriteLine($"{length},{sample},{count},{bytes / (double)count:F2},{elapsed.TotalNanoseconds / count:F1}");
        }
        async Task Exercise(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await channel.SendBinaryAsync(payload, ct);
                if (!await reader.MoveNextAsync() || reader.Current.Kind != DataChannelMessageKind.Binary ||
                    !reader.Current.Data.AsSpan().SequenceEqual(payload)) throw new IOException("SCTP benchmark receive mismatch");
            }
        }
    }
}
