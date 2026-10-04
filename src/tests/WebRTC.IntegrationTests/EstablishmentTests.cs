using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class EstablishmentTests
{
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    private static PeerConnectionOptions Options() => new()
    {
        LocalEndPoint = new(IPAddress.Loopback, 0), ConnectionTimeout = TimeSpan.FromSeconds(5),
        Dtls = new() { HandshakeTimeout = TimeSpan.FromMilliseconds(500), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100) },
        Sctp = new() { HandshakeTimeout = TimeSpan.FromMilliseconds(500), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100) }
    };
    private static EstablishmentPhaseEvidence Phase(PeerEstablishmentEvidence evidence, EstablishmentPhase phase) => evidence.Phases.Single(p => p.Phase == phase);
    private static async Task<Exception> Failure(Task task)
    {
        try { await task; } catch (Exception error) { return error; }
        throw new IOException("Silent protocol peer unexpectedly established.");
    }
    private static void Released(IPEndPoint endpoint)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp); socket.Bind(endpoint);
    }
    internal static async Task Stall(string stage)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        var options = Options() with { Dtls = Options().Dtls with { HandshakeTimeout = TimeSpan.FromSeconds(5) } };
        if (stage == "dtls") options = options with { Dtls = Options().Dtls };
        if (stage == "deadline") options = options with { ConnectionTimeout = TimeSpan.FromMilliseconds(500), Dtls = new() { HandshakeTimeout = TimeSpan.FromSeconds(5) } };
        await using var peer = new PeerConnection(options);
        await using var remote = new IceUdpTransport(new(IPAddress.Loopback, 0)); using var identity = DtlsIdentity.Generate();
        var offer = SdpNegotiation.CreateOpusOffer(new(remote.LocalCredentials, identity.GetFingerprintSha256(), remote.LocalEndPoint), 1234);
        var local = peer.CreateAnswer(offer);
        var remoteOffer = SdpSessionDescription.Parse(offer);
        var session = SdpNegotiation.ValidateOpusAnswer(remoteOffer, SdpSessionDescription.Parse(local), true);
        var endpoint = peer.GetLocalCandidates().Single(c => c.Type == IceCandidateType.Host).EndPoint;
        var connecting = peer.ConnectAsync(ct);
        await remote.ConnectAsync(session.RemoteCredentials, session.IceRole, session.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), ct);
        await using var dtls = stage == "sctp" || stage == "dcep" ?
            new DtlsSrtpTransport(remote, identity, session.DtlsRole, Convert.FromHexString(session.RemoteFingerprintSha256)) : null;
        await using var sctp = stage == "dcep" ? new SctpAssociation(dtls!, SctpRole.Responder) : null;
        if (dtls != null) { await dtls.ConnectAsync(ct); await peer.MediaReady.WaitAsync(ct); }
        Exception error;
        if (sctp != null)
        {
            await sctp.ConnectAsync(ct); await connecting;
            // SCTP receives/acknowledges DATA normally; deliberately no remote DCEP reader/ACK.
            using var opening = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            error = await Failure(peer.OpenDataChannelAsync(new("private-label-not-in-diagnostics", "secret-protocol", true, DataChannelReliability.Reliable, 0, 256), opening.Token));
            Check(error is OperationCanceledException, "DCEP cancellation was misclassified.");
        }
        else
        {
            error = await Failure(connecting); await peer.Completion.WaitAsync(ct);
            Check(error is TimeoutException, "Protocol/owner deadline must remain TimeoutException.");
        }
        var before = peer.GetEstablishmentEvidence();
        Check(Phase(before, EstablishmentPhase.Ice).Status == EstablishmentStatus.Succeeded, "ICE nomination evidence lost.");
        if (stage == "sctp" || stage == "dcep")
        {
            Check(Phase(before, EstablishmentPhase.Dtls).Status == EstablishmentStatus.Succeeded && peer.GetDiagnostics().Dtls?.Profile != null, "DTLS success/profile evidence lost.");
            Check(peer.MediaReady.IsCompletedSuccessfully, "MediaReady must be independent of SCTP/DCEP.");
        }
        var failedPhase = stage == "sctp" ? EstablishmentPhase.Sctp : stage == "dcep" ? EstablishmentPhase.Dcep : EstablishmentPhase.Dtls;
        var phase = Phase(before, failedPhase);
        Check(phase.Failure == (stage == "dcep" ? EstablishmentFailure.Cancelled : EstablishmentFailure.Timeout), "Wrong failing phase or deadline cause.");
        Check(phase.Elapsed > TimeSpan.Zero && phase.Pending == 0, "Phase duration/pending count invalid.");
        if (stage is "dtls" or "sctp") Check(phase.Retransmissions > 0 && before.Events.Any(e => e.Phase == failedPhase && e.Kind == EstablishmentEventKind.Retransmission), "No retransmission evidence.");
        await peer.DisposeAsync(); var after = peer.GetEstablishmentEvidence();
        Check(peer.State == PeerConnectionState.Closed && Phase(after, failedPhase) == phase, "Disposal overwrote failing phase/time.");
        Check(stage == "dcep" || after.TerminalState == PeerConnectionState.Failed && after.TerminalFailure == EstablishmentFailure.Timeout, "Original failure lost behind Closed state.");
        Check(after.Events.Count <= 64 && after.ClockFrequency == Stopwatch.Frequency, "Evidence bound/clock invalid.");
        if (dtls != null) Check(peer.GetDiagnostics().Dtls?.Profile != null, "Disposal erased negotiated SRTP profile.");
        Released(endpoint);
    }
    internal static async Task IceFailure(bool cancel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var a = new PeerConnection(Options() with { Ice = new() { ConnectionTimeout = TimeSpan.FromMilliseconds(400), InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100) } });
        await using var b = new PeerConnection(Options());
        a.SetRemoteAnswer(b.CreateAnswer(a.CreateOffer())); // Remote ICE remains deliberately unsignaled.
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        if (cancel) caller.CancelAfter(TimeSpan.FromMilliseconds(150));
        var error = await Failure(a.ConnectAsync(caller.Token)); await a.Completion.WaitAsync(deadline.Token);
        Check(cancel ? error is OperationCanceledException : error is TimeoutException, "ICE failure cause confused with caller cancellation.");
        await a.DisposeAsync(); var evidence = a.GetEstablishmentEvidence();
        Check(Phase(evidence, EstablishmentPhase.Ice).Failure == (cancel ? EstablishmentFailure.Cancelled : EstablishmentFailure.Timeout), "ICE failure evidence changed on disposal.");
        Check(Phase(evidence, EstablishmentPhase.Dtls).Status == EstablishmentStatus.NotStarted, "DTLS started without ICE nomination.");
    }
    internal static async Task AuthenticationFailure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var a = new PeerConnection(Options() with { Dtls = new() });
        await using var b = new PeerConnection(Options() with { Dtls = new() });
        using var unrelated = DtlsIdentity.Generate();
        var answer = b.CreateAnswer(a.CreateOffer());
        var fingerprint = string.Join(":", Convert.FromHexString(SdpSessionDescription.Parse(answer).Media[0].FingerprintSha256!).Select(v => v.ToString("X2")));
        var wrong = string.Join(":", unrelated.GetFingerprintSha256().Select(v => v.ToString("X2")));
        a.SetRemoteAnswer(answer.Replace(fingerprint, wrong, StringComparison.Ordinal));
        var right = b.ConnectAsync(ct); var left = a.ConnectAsync(ct);
        Check(await Failure(left) is System.Security.Authentication.AuthenticationException, "Wrong certificate fingerprint must still fail authentication.");
        await a.Completion.WaitAsync(ct); await a.DisposeAsync(); await b.DisposeAsync();
        try { await right; } catch (Exception) { }
        Check(Phase(a.GetEstablishmentEvidence(), EstablishmentPhase.Dtls).Failure == EstablishmentFailure.Authentication, "Authentication evidence lost on cleanup.");
    }
    internal static async Task Repeated(SdpSetup setup)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var ct = deadline.Token;
        for (var n = 0; n < 4; n++)
        {
            await using var a = new PeerConnection(Options() with { Dtls = new(), Sctp = new() });
            await using var b = new PeerConnection(Options() with { Dtls = new(), Sctp = new() });
            var untouched = a.GetEstablishmentEvidence();
            var answer = b.CreateAnswer(a.CreateOffer(), setup); a.SetRemoteAnswer(answer);
            var endpoint = a.GetLocalCandidates().Single(c => c.Type == IceCandidateType.Host).EndPoint;
            await Task.WhenAll(a.ConnectAsync(ct), b.ConnectAsync(ct));
            var channel = await a.OpenDataChannelAsync(new("events", "", true, DataChannelReliability.Reliable, 0, 256), ct);
            var evidence = a.GetEstablishmentEvidence();
            Check(evidence.Phases.All(p => p.Status == EstablishmentStatus.Succeeded), "Repeated establishment phase missing.");
            Check(evidence.DtlsRole == (setup == SdpSetup.Active ? DtlsRole.Server : DtlsRole.Client), "DTLS role attribution incorrect.");
            Check(untouched.Events.Count == 0 && untouched.Phases.All(p => p.Attempts == 0), "Snapshot mutated after capture.");
            await a.DisposeAsync(); Released(endpoint);
            Check(a.GetEstablishmentEvidence().Phases.All(p => p.Status == EstablishmentStatus.Succeeded), "Clean disposal erased protocol success.");
            _ = channel;
        }
    }
    internal static async Task Bounds()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var ct = deadline.Token;
        await using var a = new PeerConnection(Options() with { Dtls = new(), Sctp = new() });
        await using var b = new PeerConnection(Options() with { Dtls = new(), Sctp = new() });
        a.SetRemoteAnswer(b.CreateAnswer(a.CreateOffer())); await Task.WhenAll(a.ConnectAsync(ct), b.ConnectAsync(ct));
        await using var accepted = b.AcceptDataChannelsAsync(ct).GetAsyncEnumerator(ct);
        for (var n = 0; n < 36; n++)
        {
            var receive = accepted.MoveNextAsync().AsTask();
            await a.OpenDataChannelAsync(new("private", "private", true, DataChannelReliability.Reliable, 0, 256), ct);
            Check(await receive, "Remote channel admission ended.");
        }
        var evidence = a.GetEstablishmentEvidence();
        Check(evidence.Events.Count == 64 && evidence.EventsOverwritten > 0, "Establishment history must remain bounded.");
        Check(Phase(evidence, EstablishmentPhase.Dcep) is { Attempts: 36, Succeeded: 36, Pending: 0 }, "Exact DCEP totals lost on history wrap.");
        Check(evidence.Events.Zip(evidence.Events.Skip(1)).All(pair => pair.First.TimestampTicks <= pair.Second.TimestampTicks), "Journal order inconsistent.");
        Check(!evidence.ToString().Contains("private", StringComparison.Ordinal), "Private channel metadata entered diagnostics.");
    }
}
