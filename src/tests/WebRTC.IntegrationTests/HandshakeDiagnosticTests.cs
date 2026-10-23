using System.Net;
using tryAGI.WebRTC;

internal static class HandshakeDiagnosticTests
{
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    private static PacketStageEvent[] Drain(PeerDiagnosticSession capture)
    {
        var events = new PacketStageEvent[capture.TraceCapacity];
        return events[..capture.Drain(events)];
    }
    internal static async Task Exchange(bool silent, string captureMode = "complete")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        var captureOptions = new PeerDiagnosticsOptions
        {
            Metrics = false, PacketTrace = true, EventCapacity = captureMode == "wrapped" ? 8 : 512,
            CaptureDuration = captureMode == "expired" ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(30),
        };
        using var sentCapture = left.AttachDiagnostics(captureOptions);
        using var receivedCapture = right.AttachDiagnostics(captureOptions);
        using var clientIdentity = DtlsIdentity.Generate(); using var serverIdentity = DtlsIdentity.Generate();
        var options = new DtlsSrtpOptions
        {
            HandshakeTimeout = silent ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(4),
            InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var client = new DtlsSrtpTransport(left, clientIdentity, DtlsRole.Client, serverIdentity.GetFingerprintSha256(), options);
        await using var server = silent ? null : new DtlsSrtpTransport(right, serverIdentity, DtlsRole.Server, clientIdentity.GetFingerprintSha256(), options);
        Check(client.GetHandshakeCounters() == new DtlsHandshakeCounters(), "Fresh handshake counters were not empty");
        if (silent)
        {
            try { await client.ConnectAsync(ct); throw new IOException("Silent remote established DTLS"); }
            catch (TimeoutException) { }
            Check(client.GetDiagnostics().Retransmissions > 0, "No handshake retry exercised");
            // Drain actual datagrams at the independent remote ICE boundary, not just local send events.
            for (var n = 0; n <= client.GetDiagnostics().Retransmissions; n++)
            {
                await using var reader = right.ReceiveDatagramsAsync(ct).GetAsyncEnumerator(ct);
                Check(await reader.MoveNextAsync() && reader.Current[0] == 22 && reader.Current[13] == 1,
                    "Expected a DTLS ClientHello at the nominated remote socket");
            }
        }
        else await Task.WhenAll(client.ConnectAsync(ct), server!.ConnectAsync(ct));
        while (!client.GetHandshakeCounters().Frozen) await Task.Delay(1, ct);
        var counters = client.GetHandshakeCounters();
        Check(counters.Version == 1 && counters.CompletedRecordSends > 0 &&
            counters.RecordSendAttempts >= counters.CompletedRecordSends && counters.CompletedFlights > 0,
            "Handshake send counters missing");
        if (silent)
            Check(counters.ReceivedDatagrams == 0 && counters.VerifiedFinishedMessages == 0,
                "Silent peer fabricated receive/authentication evidence");
        else
        {
            Check(counters.ReceivedDatagrams > 0 && counters.ReceivedRecords > 0 && counters.ValidatedFragments > 0 &&
                counters.ReassembledMessages >= counters.AcceptedMessages && counters.AcceptedMessages > 0 &&
                counters.ReceivedChangeCipherSpecs > 0 && counters.VerifiedFinishedMessages == 1,
                "Successful handshake counters missing");
            await client.SendApplicationDatagramAsync("after-handshake-freeze"u8.ToArray(), ct);
            Check((await DtlsTests.App(server ?? throw new IOException("Missing server"), ct)).AsSpan().SequenceEqual("after-handshake-freeze"u8),
                "Application data failed after handshake freeze");
            Check(client.GetHandshakeCounters() == counters, "Application data mutated frozen handshake counters");
        }
        await client.DisposeAsync();
        Check(client.GetHandshakeCounters() == counters, "Handshake counters changed during disposal");
        if (captureMode != "complete")
        {
            if (captureMode == "wrapped")
                Check(sentCapture.GetSnapshot().TraceEventsDropped > 0, "Small trace ring did not overflow");
            else Check(sentCapture.GetSnapshot().State == DiagnosticCaptureState.Expired, "Short trace did not expire");
            sentCapture.Dispose(); receivedCapture.Dispose();
            Check(client.GetHandshakeCounters() == counters, "Trace destruction erased retained handshake counters");
            return;
        }
        var sent = Drain(sentCapture); var received = Drain(receivedCapture);
        var sends = sent.Where(e => e.Direction == PacketDirection.Send && e.Stage == PacketStage.SocketSendCompleted && e.Protocol == DiagnosticProtocol.Dtls).ToArray();
        Check(sends.Length > 0 && sends.All(e => e.Bytes > 13 && e.Reason == PacketReason.None), "Missing completed DTLS socket sends");
        // Trace rings intentionally never block transport and can lose counted records.
        Check(sends.Any(e => sent.Any(start => start.PacketId == e.PacketId && start.Stage == PacketStage.SocketSendStarted && start.TimestampTicks <= e.TimestampTicks)), "No correlated send boundaries");
        Check(received.Any(e => e.Stage == PacketStage.IceEnqueued && e.Protocol == DiagnosticProtocol.Dtls), "Received DTLS classification missing at ICE admission");
        Check(received.Any(e => e.Stage == PacketStage.IceDequeued && e.Protocol == DiagnosticProtocol.Dtls), "Received DTLS classification missing at ICE dequeue");
        if (silent)
        {
            var completed = sentCapture.GetEventCounts().Single(e => e.Direction == PacketDirection.Send && e.Stage == PacketStage.SocketSendCompleted && e.Reason == PacketReason.None).Count;
            Check(completed == client.GetDiagnostics().Retransmissions + 1, "Initial ClientHello or retry not counted exactly once");
            Check(!sent.Any(e => e.Direction == PacketDirection.Receive && e.Protocol == DiagnosticProtocol.Dtls), "Silent remote fabricated DTLS receive evidence");
        }
        else Check(sent.Any(e => e.Direction == PacketDirection.Receive && e.Stage == PacketStage.IceDequeued && e.Protocol == DiagnosticProtocol.Dtls), "Server flight receipt missing");
        Check(sent.Concat(received).All(e => e.Source == null && e.Sequence == null && e.RtpTimestamp == null && e.SourceEpoch == null), "Identifiers leaked with default capture policy");
        Check(sentCapture.GetSnapshot().RecordedEvents >= sent.Length && receivedCapture.GetSnapshot().RecordedEvents >= received.Length,
            "Trace sample exceeds aggregate record coverage");
    }
}
