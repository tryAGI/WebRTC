using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class IceAdmissionDiagnosticTests
{
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    internal static async Task Rejection(IceDatagramRejectionReason reason)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var fixture = reason == IceDatagramRejectionReason.PathMismatch ? new TurnFixture(modern: true) : null;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new() { MaximumDataDatagramSize = 4096 });
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new()
        { RelayOnly = reason == IceDatagramRejectionReason.LocalPathUnavailable });
        using var capture = right.AttachDiagnostics(new() { PacketTrace = true, Metrics = false, EventCapacity = 8 });
        var untouched = right.GetDatagramRejectionCounts();
        IceCandidate? relay = null;
        if (fixture != null) relay = await right.GatherRelayCandidateAsync(fixture.Server, new(TurnFixture.Username, TurnFixture.Secret), TurnFixture.Fast(), ct);
        var connected = reason is not (IceDatagramRejectionReason.NoNominatedPair or IceDatagramRejectionReason.LocalPathUnavailable);
        if (connected)
        {
            await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
                right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
            Check(right.GetDiagnostics().SelectedLocalCandidateType == IceCandidateType.Host, "Fixture must nominate the host path");
        }
        using var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        attacker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var packet = new byte[reason is IceDatagramRejectionReason.Oversized or IceDatagramRejectionReason.SourceMismatch ? 2049 : 13]; packet[0] = 22;
        if (fixture != null) while (fixture.Permissions == 0) await Task.Delay(5, ct);
        async Task Inject()
        {
            if (reason == IceDatagramRejectionReason.Oversized) await left.SendDatagramAsync(packet, ct);
            else await attacker.SendToAsync(packet, SocketFlags.None, relay?.EndPoint ?? right.LocalEndPoint, ct);
        }
        await Inject();
        while (right.GetDiagnostics().DroppedDatagrams == 0) await Task.Delay(5, ct);
        var counts = right.GetDatagramRejectionCounts();
        Check(counts.Count == 7 && counts.Sum(c => c.Count) == 1 && counts.Single(c => c.Reason == reason).Count == 1,
            "ICE rejected datagram was attributed to the wrong first failing predicate");
        Check(untouched.All(c => c.Count == 0), "Previously captured rejection snapshot mutated");
        Check(capture.GetEventCounts().Any(e => e.Stage == PacketStage.Dropped && e.Reason == PacketReason.InvalidRouteOrConsent && e.Count == 1),
            "Existing trace category changed or failed to count rejection");
        var first = counts;
        for (var n = 0; n < 3; n++) await Inject();
        while (right.GetDiagnostics().DroppedDatagrams < 4) await Task.Delay(5, ct);
        counts = right.GetDatagramRejectionCounts();
        Check(counts.Sum(c => c.Count) == 4 && counts.Single(c => c.Reason == reason).Count == 4 && first.Sum(c => c.Count) == 1,
            "Truncation lost exact counts or mutated a previous snapshot");
        // Overflow the eight-event trace ring deliberately. Exact reason counters are independent of capture loss.
        Check(capture.GetSnapshot().TraceEventsDropped > 0, "Small trace fixture did not exercise truncation");
        if (connected)
        {
            await left.SendDatagramAsync(new byte[] { 22, 1, 2, 3 }, ct);
            await using var accepted = right.ReceiveDatagramsAsync(ct).GetAsyncEnumerator(ct);
            Check(await accepted.MoveNextAsync() && accepted.Current.AsSpan().SequenceEqual(new byte[] { 22, 1, 2, 3 }),
                "Valid nominated-source data stopped passing after rejection");
            Check(right.GetDatagramRejectionCounts().SequenceEqual(counts), "Accepted packet was counted as rejected");
        }
        await right.DisposeAsync();
        Check(right.GetDatagramRejectionCounts().SequenceEqual(counts), "Disposal erased data rejection reason counts");
        Check(capture.GetSnapshot().State == DiagnosticCaptureState.Disposed, "Packet trace did not follow normal disposal lifecycle");
    }
}
