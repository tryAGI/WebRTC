using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using tryAGI.WebRTC;

internal static class DiagnosticTests
{
    private static void Check(bool value, string message = "Diagnostics assertion failed") { if (!value) throw new IOException(message); }
    private static byte[] Payload() => Convert.FromHexString("7881A8B036089FC201D66EF7DFFADA025AF3F4969ED2892A0995E48742F90670483DAD77C7F0A9A749175731FD11D709FF8D7B1B5F70A9480AA33804");
    private static PeerConnectionOptions Options(int capacity = 32) => new()
    { LocalEndPoint = new(IPAddress.Loopback, 0), DataChannels = false, AudioQueueCapacity = capacity, ConnectionTimeout = TimeSpan.FromSeconds(5) };
    private static PeerDiagnosticsOptions Capture(bool identities = true) => new()
    { PacketTrace = true, IncludePacketIdentifiers = identities, CaptureDuration = TimeSpan.FromSeconds(20), EventCapacity = 4096 };
    private static async Task Connect(PeerConnection a, PeerConnection b, CancellationToken ct)
    { a.SetRemoteAnswer(b.CreateAnswer(a.CreateOffer())); await Task.WhenAll(a.ConnectAsync(ct), b.ConnectAsync(ct)); }
    private static async Task<EncodedOpusPacket> First(PeerConnection peer, CancellationToken ct)
    { await foreach (var packet in peer.ReceiveAudioAsync(ct)) return packet; throw new IOException("Audio ended"); }
    private static PacketStageEvent[] Drain(PeerDiagnosticSession session)
    { var events = new PacketStageEvent[session.TraceCapacity]; var count = session.Drain(events); return events[..count]; }

    internal static async Task Stages(TurnServerTransport? relay)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var ct = timeout.Token;
        await using var udp = relay == TurnServerTransport.Udp ? new TurnFixture(modern: true) : null;
        await using var stream = relay is TurnServerTransport.Tcp or TurnServerTransport.Tls ? new TurnStreamFixture(relay == TurnServerTransport.Tls) : null;
        await using var a = new PeerConnection(Options() with { Ice = new() { RelayOnly = relay != null } });
        await using var b = new PeerConnection(Options());
        using var left = a.AttachDiagnostics(Capture()); using var right = b.AttachDiagnostics(Capture());
        if (relay != null) await a.GatherRelayCandidateAsync(udp?.Server ?? stream!.Server, new(TurnFixture.Username, TurnFixture.Secret), stream?.Options ?? TurnFixture.Fast(), ct);
        await Connect(a, b, ct); var payload = Payload();
        await a.SendOpusAsync(payload, uint.MaxValue - 959, true, ct); await a.SendOpusAsync(payload, 0, false, ct);
        await First(b, ct); await First(b, ct);
        for (uint n = 0; n < 8; n++)
        {
            await b.SendOpusAsync(payload, 960 + n * 960, cancellationToken: ct); await First(a, ct);
            if (relay == null) { await a.SendOpusAsync(payload, 960 + n * 960, cancellationToken: ct); await First(b, ct); }
        }
        var incoming = Drain(relay == null ? right : left);
        var required = new[] { PacketStage.Demultiplexed, PacketStage.IceEnqueued, PacketStage.IceDequeued,
            PacketStage.AuthenticationStarted, PacketStage.Authenticated, PacketStage.SecureMediaEnqueued, PacketStage.SecureMediaDequeued,
            PacketStage.AudioEnqueued, PacketStage.AudioDequeued, PacketStage.ConsumerDelivery };
        var deliveries = incoming.Where(e => e.Stage == PacketStage.ConsumerDelivery).ToArray();
        // Nonblocking capture explicitly permits counted record loss. Require a complete correlated packet,
        // rather than assuming the last packet won every concurrent ring admission.
        var delivery = deliveries.FirstOrDefault(d => required.All(stage => incoming.Any(e => e.PacketId == d.PacketId && e.Stage == stage)));
        Check(delivery.Stage == PacketStage.ConsumerDelivery, "No complete packet timeline; capture drops=" + (relay == null ? right : left).GetSnapshot().TraceEventsDropped);
        var stages = incoming.Where(e => e.PacketId == delivery.PacketId).ToArray();
        var ordered = required.Select(stage => stages.Single(e => e.Stage == stage)).ToArray();
        Check(ordered.Zip(ordered.Skip(1)).All(pair => pair.First.TimestampTicks <= pair.Second.TimestampTicks));
        Check(delivery.Source != null && delivery.Sequence != null && delivery.RtpTimestamp != null && delivery.SourceEpoch != null);
        var expected = relay switch { TurnServerTransport.Udp => DiagnosticPath.TurnUdp, TurnServerTransport.Tcp => DiagnosticPath.TurnTcp,
            TurnServerTransport.Tls => DiagnosticPath.TurnTls, _ => DiagnosticPath.HostUdp };
        Check(delivery.Path == expected && delivery.PathGeneration == 1);
        Check(incoming.Any(e => e.Stage == (relay is TurnServerTransport.Tcp or TurnServerTransport.Tls ? PacketStage.StreamReadCompleted : PacketStage.ManagedReceiveCompleted)));
        Check(incoming.Any(e => e.Stage == PacketStage.ReceiveHandlerCompleted) && incoming.Any(e => e.Stage == PacketStage.ReceiveRearm));
        var outgoing = relay == null ? Drain(left) : incoming;
        foreach (var stage in new[] { PacketStage.CallerSubmission, PacketStage.SendLockAcquired, PacketStage.SecureSendLockAcquired,
            PacketStage.ProtectionStarted, PacketStage.Protected, PacketStage.SocketSendStarted, PacketStage.SocketSendCompleted })
            Check(outgoing.Any(e => e.Stage == stage), "Missing send stage " + stage);
        Check(left.Clock.Frequency == Stopwatch.Frequency && !left.Clock.KernelReceiveTimestampSupported && !left.Clock.SocketOverflowCounterSupported);
        var route = a.GetRouteEvidence(); Check(route.Path == expected && route.PathGeneration == 1 && route.LocalEndPoint == null && route.RemoteEndPoint == null);
        Check(!route.AudioRecoveryObservable && !route.RetransmissionNegotiated && route.LastAuthenticatedMediaAge != null);
        Check(a.GetRouteEvidence(true).LocalEndPoint != null);
        var evidence = b.GetRtpEvidence(); Check(evidence.ReceivedStreams.Count == 1 && evidence.ReceivedStreams[0].Source == null);
        Check(evidence.ReceivedStreams[0].ClockRate == 48000 && evidence.ReceivedStreams[0].Ready);
        Check(b.GetRtpEvidence(true).ReceivedStreams[0].Source == a.AudioSource);
        // The socket/stream reader is already armed with the previous session.
        using var live = a.AttachDiagnostics(Capture());
        await b.SendOpusAsync(payload, 30000, cancellationToken: ct); await First(a, ct);
        Check(live.GetSnapshot().LastAuthenticatedTicks != null, "Live replacement missed its first arriving packet");
    }

    internal static async Task QueueAndLifecycle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = timeout.Token;
        await using var a = new PeerConnection(Options()); await using var b = new PeerConnection(Options(2));
        await Connect(a, b, ct); var payload = Payload();
        // Live attach, no reconnect; detailed identifiers are off by default.
        using var capture = b.AttachDiagnostics(Capture(false));
        for (uint i = 0; i < 12; i++) await a.SendOpusAsync(payload, i * 960, cancellationToken: ct);
        var timer = Stopwatch.StartNew(); while (b.GetDiagnostics().DroppedAudioPackets != 10 && timer.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(5, ct);
        Check(b.GetDiagnostics().DroppedAudioPackets == 10);
        await Task.Delay(40, ct);
        var queue = b.GetQueueEvidence().Single(q => q.EnqueueBoundary == PacketStage.AudioEnqueued);
        Check(queue.Depth == 2 && queue.ObservedHighWater == 2 && queue.OldestAge > TimeSpan.FromMilliseconds(20));
        await First(b, ct); await First(b, ct);
        var events = Drain(capture);
        Check(events.Any(e => e.Stage == PacketStage.Dropped && e.Reason == PacketReason.QueueOverflow));
        Check(events.Any(e => e.Stage == PacketStage.AudioDequeued && e.DurationTicks > Stopwatch.Frequency / 50));
        Check(capture.GetSnapshot().AudioQueueHighWater == 2);
        Check(events.All(e => e.Source == null && e.Sequence == null && e.RtpTimestamp == null));
        b.DetachDiagnostics(); Check(capture.GetSnapshot().State == DiagnosticCaptureState.Disposed);
        await a.SendOpusAsync(payload, 20000, cancellationToken: ct); await First(b, ct);
        using var tiny = b.AttachDiagnostics(Capture() with { EventCapacity = 8, MaximumBufferBytes = 32768 });
        for (uint i = 0; i < 10; i++) { await a.SendOpusAsync(payload, 21000 + i * 960, cancellationToken: ct); await First(b, ct); }
        var snapshot = tiny.GetSnapshot(); Check(snapshot.BufferedTraceEvents <= 8 && snapshot.TraceEventsDropped > 0 && snapshot.MetricEventsDropped > 0);
        Check(tiny.BufferBudgetBytes <= 32768 && b.GetDiagnostics().DroppedAudioPackets == 10);
        using var expiry = b.AttachDiagnostics(Capture() with { CaptureDuration = TimeSpan.FromMilliseconds(20) });
        await Task.Delay(40, ct); Check(expiry.GetSnapshot().State == DiagnosticCaptureState.Expired);
        var count = expiry.GetSnapshot().RecordedEvents; await a.SendOpusAsync(payload, 40000, cancellationToken: ct); await First(b, ct);
        Check(expiry.GetSnapshot().RecordedEvents == count);
        using var cancel = new CancellationTokenSource(); using var cancelled = b.AttachDiagnostics(Capture(), cancel.Token);
        cancel.Cancel(); Check(cancelled.GetSnapshot().State == DiagnosticCaptureState.Cancelled && cancelled.GetSnapshot().BufferedTraceEvents == 0);
        using var fresh = b.AttachDiagnostics(Capture()); Check(fresh.PeerEpoch == capture.PeerEpoch && fresh.CaptureEpoch != capture.CaptureEpoch);
        await b.DisposeAsync(); Check(fresh.GetSnapshot().State == DiagnosticCaptureState.Disposed);
        await using var next = new PeerConnection(Options()); using var reconnect = next.AttachDiagnostics(Capture()); Check(reconnect.PeerEpoch != capture.PeerEpoch);
    }

    internal static async Task CollectorIsolation(bool slow)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        await using var a = new PeerConnection(Options()); await using var b = new PeerConnection(Options());
        await Connect(a, b, ct); using var capture = b.AttachDiagnostics(Capture());
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var listener = new MeterListener(); var tagsSeen = new ConcurrentBag<string>();
        listener.InstrumentPublished = (instrument, meterListener) => { if (instrument.Meter.Name == PeerDiagnosticSession.InstrumentationName) meterListener.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags) tagsSeen.Add(tag.Key);
            entered.Set(); if (slow) release.Wait(TimeSpan.FromSeconds(5)); else throw new InvalidOperationException("Deliberately failing collector");
        }); listener.Start();
        await a.SendOpusAsync(Payload(), 960, cancellationToken: ct); await First(b, ct);
        var publication = Task.Run(capture.Publish); Check(entered.Wait(TimeSpan.FromSeconds(2)));
        await a.SendOpusAsync(Payload(), 1920, cancellationToken: ct);
        Check((await First(b, ct)).Timestamp == 1920, "Collector stalled audio");
        await b.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        release.Set(); await publication.WaitAsync(ct); capture.DisposePublisher();
        Check(tagsSeen.All(tag => tag is "direction" or "stage" or "reason"));
        if (!slow) Check(capture.GetSnapshot().CollectorFailures > 0);
    }

    internal static Task ReceptionScopes()
    {
        var tracker = new RtpReceptionTracker(3, 48000);
        tracker.Observe(65535, uint.MaxValue - 959, TimeSpan.Zero); tracker.Observe(0, 0, TimeSpan.FromMilliseconds(20));
        var first = tracker.GetSnapshot(); Check(first.HighestSequence == 65536 && first.ResetEpoch == 1 && first.CumulativeLost == 0);
        tracker.Observe(2, 1920, TimeSpan.FromMilliseconds(60)); Check(tracker.GetSnapshot().CumulativeLost == 1);
        tracker.Observe(1, 960, TimeSpan.FromMilliseconds(65)); Check(tracker.GetSnapshot().LastReason == PacketReason.Reordered);
        tracker.Observe(2, 1920, TimeSpan.FromMilliseconds(70)); Check(tracker.GetSnapshot().LastReason == PacketReason.Duplicate);
        var before = tracker.GetSnapshot(); var after = tracker.GetSnapshot(); Check(before == after);
        var report = tracker.CreateReport(); Check(report?.FractionLost == before.FractionLost);
        tracker.Observe(30000, 4000, TimeSpan.FromMilliseconds(100)); tracker.Observe(30001, 4960, TimeSpan.FromMilliseconds(120));
        Check(tracker.GetSnapshot().ResetEpoch == 2 && tracker.GetSnapshot().HighestSequence == 30001);
        var another = new RtpReceptionTracker(4, 48000); another.Observe(9, 1, TimeSpan.Zero); another.Observe(10, 961, TimeSpan.FromMilliseconds(20));
        Check(another.GetSnapshot().ResetEpoch == 1 && another.GetSnapshot().CumulativeLost == 0);
        return Task.CompletedTask;
    }

    internal static async Task CallerPacing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        await using var a = new PeerConnection(Options()); await using var b = new PeerConnection(Options()); await Connect(a, b, ct);
        using var capture = a.AttachDiagnostics(Capture());
        await a.SendOpusAsync(Payload(), uint.MaxValue - 959, cancellationToken: ct); await First(b, ct);
        await Task.Delay(80, ct); await a.SendOpusAsync(Payload(), 0, cancellationToken: ct); await First(b, ct);
        await a.SendOpusAsync(Payload(), 9600, cancellationToken: ct); await First(b, ct);
        var events = Drain(capture); var submitted = events.Where(e => e.Stage == PacketStage.CallerSubmission && e.Protocol == DiagnosticProtocol.AudioRtp).ToArray();
        Check(submitted.Length == 3 && submitted[1].TimestampTicks - submitted[0].TimestampTicks > Stopwatch.Frequency / 20,
            "Caller pacing delay was not visible between submissions");
        var completed = events.Where(e => e.Stage == PacketStage.SocketSendCompleted && e.Protocol == DiagnosticProtocol.AudioRtp).ToArray();
        Check(completed.Length == 3 && completed.All(e => e.DurationTicks < Stopwatch.Frequency / 20), "Caller delay was attributed to local socket send");
        Check(events.Any(e => e.Reason == PacketReason.CatchUpBurst && e.RtpTimestamp == 9600), "Advancing media-clock catch-up burst was not identified");
    }

    internal static async Task SamplingAndActivities()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        await using var a = new PeerConnection(Options()); await using var b = new PeerConnection(Options()); await Connect(a, b, ct);
        using (var disabled = b.AttachDiagnostics(new() { Metrics = false, PacketTrace = false }))
        {
            await a.SendOpusAsync(Payload(), 960, cancellationToken: ct); await First(b, ct);
            Check(disabled.GetSnapshot().RecordedEvents == 0 && disabled.TraceCapacity == 0 && disabled.MetricCapacity == 0);
        }
        using (var sampled = b.AttachDiagnostics(new() { Metrics = false, PacketTrace = true, SampleEvery = 10000, AnomalyThreshold = TimeSpan.FromMilliseconds(10) }))
        {
            await a.SendOpusAsync(Payload(), 1920, cancellationToken: ct);
            await Task.Delay(40, ct); await First(b, ct);
            var observed = Drain(sampled);
            Check(observed.Any(e => e.Stage == PacketStage.AudioDequeued), "Threshold did not retain an unsampled queue anomaly");
            Check(observed.All(e => e.Source == null && e.Sequence == null));
        }
        using var capture = b.AttachDiagnostics(Capture() with { AnomalyThreshold = TimeSpan.FromMilliseconds(10) });
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PeerDiagnosticSession.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => throw new InvalidOperationException("Throwing Activity collector"),
        };
        ActivitySource.AddActivityListener(activities);
        await a.SendOpusAsync(Payload(), 2880, cancellationToken: ct); await Task.Delay(40, ct); await First(b, ct);
        var parent = Activity.Current; var beforePublish = capture.GetSnapshot(); capture.Publish();
        Check(capture.GetSnapshot().CollectorFailures > 0, "Activity failure was not observed; metric samples=" + beforePublish.BufferedMetricEvents);
        Check(Activity.Current == parent, "Activity collector changed caller context");
        await a.SendOpusAsync(Payload(), 3840, cancellationToken: ct); await First(b, ct);
        capture.DisposePublisher();
        for (var n = 0; n < 10; n++)
        {
            using var cancel = new CancellationTokenSource(); using var live = b.AttachDiagnostics(Capture(), cancel.Token);
            await Task.WhenAll(Task.Run(cancel.Cancel), Task.Run(b.DetachDiagnostics));
            Check(live.GetSnapshot().State == DiagnosticCaptureState.Disposed);
        }
    }

    internal static async Task Performance()
    {
        using var experiment = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, collector) => { if (instrument.Meter.Name == PeerDiagnosticSession.InstrumentationName) collector.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((i, m, t, s) => { }); listener.SetMeasurementEventCallback<double>((i, m, t, s) => { }); listener.SetMeasurementEventCallback<int>((i, m, t, s) => { }); listener.Start();
        var modes = new[] { "off", "aggregate", "trace" };
        for (var round = 0; round < 3; round++)
        for (var index = 0; index < 3; index++)
        {
            var mode = modes[(round + index) % modes.Length];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(experiment.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
            await using var a = new PeerConnection(Options()); await using var b = new PeerConnection(Options()); await Connect(a, b, ct);
            using var capture = mode == "off" ? null : b.AttachDiagnostics(Capture() with { PacketTrace = mode == "trace" });
            var payload = Payload();
            using (var warmup = new PeriodicTimer(TimeSpan.FromMilliseconds(20)))
            for (uint n = 0; n < 50; n++)
            { await warmup.WaitForNextTickAsync(ct); await a.SendOpusAsync(payload, n * 960, cancellationToken: ct); await First(b, ct); }
            PacketStageEvent[] traceBuffer = capture == null ? [] : new PacketStageEvent[capture.TraceCapacity];
            capture?.Publish(); capture?.Drain(traceBuffer);
            const int packets = 100; var latencies = new double[packets];
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var total = Stopwatch.StartNew(); using var pacing = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            for (uint n = 0; n < packets; n++)
            {
                await pacing.WaitForNextTickAsync(ct); var submitted = Stopwatch.GetTimestamp();
                await a.SendOpusAsync(payload, (n + 50) * 960, cancellationToken: ct); await First(b, ct);
                latencies[n] = Stopwatch.GetElapsedTime(submitted).TotalMilliseconds;
                if (n % 20 == 19) { capture?.Publish(); capture?.Drain(traceBuffer); }
            }
            var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated; var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            Array.Sort(latencies);
            Console.WriteLine($"DIAGNOSTIC_BENCH round={round + 1} mode={mode} packets={packets} payload_bytes={payload.Length} elapsed_ms={total.Elapsed.TotalMilliseconds:F2} cpu_ms={cpu.TotalMilliseconds:F2} allocated_bytes={bytes} bytes_per_packet={bytes / (double)packets:F2} delivery_p50_ms={latencies[49]:F3} delivery_p99_ms={latencies[98]:F3}");
            capture?.DisposePublisher();
        }
    }
}
