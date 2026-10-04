using System.Net;
using tryAGI.WebRTC;

// Standards-authored local fixture; also compiled against the published 0.2.2 baseline.
internal static class PeerSctpRoleTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new IOException(message);
    }

    private static async Task<T> First<T>(IAsyncEnumerable<T> source, CancellationToken ct)
    {
        await foreach (var value in source.WithCancellation(ct)) return value;
        throw new IOException("Remote stream ended before its first item.");
    }

    internal static async Task Exchange(SdpSetup setup, SctpRole remoteRole, bool expectPassiveTimeout = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var sctpOptions = new SctpOptions
        {
            HandshakeTimeout = TimeSpan.FromSeconds(2),
            InitialRetransmissionTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var peer = new PeerConnection(new()
        {
            LocalEndPoint = new(IPAddress.Loopback, 0), ConnectionTimeout = TimeSpan.FromSeconds(8),
            Dtls = new() { HandshakeTimeout = TimeSpan.FromSeconds(5) }, Sctp = sctpOptions
        });
        await using var ice = new IceUdpTransport(new(IPAddress.Loopback, 0));
        using var identity = DtlsIdentity.Generate();
        var offer = SdpSessionDescription.Parse(peer.CreateOffer());
        var answerText = SdpNegotiation.CreateOpusAnswer(offer,
            new(ice.LocalCredentials, identity.GetFingerprintSha256(), ice.LocalEndPoint),
            1234, preferredSetup: setup);
        peer.SetRemoteAnswer(answerText);
        var session = SdpNegotiation.ValidateOpusAnswer(offer, SdpSessionDescription.Parse(answerText), false);
        await using var dtls = new DtlsSrtpTransport(ice, identity, session.DtlsRole,
            Convert.FromHexString(session.RemoteFingerprintSha256));
        // The passive baseline's remote endpoint must stay alive beyond the owned SCTP deadline.
        await using var sctp = new SctpAssociation(dtls, remoteRole,
            sctpOptions with { HandshakeTimeout = TimeSpan.FromSeconds(8) });
        using var remoteLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remoteDtlsReady = false;
        var baselineReproduced = false;
        async Task ConnectRemote()
        {
            await ice.ConnectAsync(session.RemoteCredentials, session.IceRole,
                session.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), remoteLifetime.Token);
            await dtls.ConnectAsync(remoteLifetime.Token);
            remoteDtlsReady = true;
            await sctp.ConnectAsync(remoteLifetime.Token);
        }
        var remoteConnecting = ConnectRemote();
        var connecting = peer.ConnectAsync(ct);
        try
        {
            if (expectPassiveTimeout)
            {
                Check(setup == SdpSetup.Active && remoteRole == SctpRole.Responder,
                    "Only DTLS-server/passive-SCTP reproduces the published defect.");
                try
                {
                    await connecting;
                    throw new IOException("Published 0.2.2 unexpectedly initiated SCTP as a DTLS server.");
                }
                catch (TimeoutException)
                {
                    await peer.Completion.WaitAsync(ct);
                }
                var evidence = peer.GetEstablishmentEvidence();
                Check(peer.MediaReady.IsCompletedSuccessfully && remoteDtlsReady,
                    "Baseline must fail after successful ICE/DTLS, not during media establishment.");
                Check(evidence.DtlsRole == DtlsRole.Server && evidence.SctpRole == SctpRole.Responder,
                    "Baseline passive/passive roles were not reproduced.");
                Check(evidence.Phases.Where(p => p.Phase is EstablishmentPhase.Ice or EstablishmentPhase.Dtls)
                    .All(p => p.Status == EstablishmentStatus.Succeeded), "Baseline ICE/DTLS did not succeed.");
                var failed = evidence.Phases.Single(p => p.Phase == EstablishmentPhase.Sctp);
                Check(failed.Failure == EstablishmentFailure.Timeout && failed.Step == HandshakeStep.SctpInit &&
                    failed.Retransmissions == 0 && !remoteConnecting.IsCompletedSuccessfully,
                    "Baseline must time out waiting for INIT with two passive endpoints.");
                baselineReproduced = true;
                Console.WriteLine("Published 0.2.2 defect reproduced: ICE/DTLS succeeded; passive/passive SCTP INIT timeout, zero retries.");
                return;
            }

            await Task.WhenAll(connecting, remoteConnecting);
            var observed = peer.GetEstablishmentEvidence();
            Check(observed.DtlsRole == (setup == SdpSetup.Active ? DtlsRole.Server : DtlsRole.Client),
                "Unexpected owned DTLS role.");
            Check(observed.SctpRole == SctpRole.Initiator, "Owned SCTP must initiate regardless of DTLS role.");
            Check(observed.Phases.Single(p => p.Phase == EstablishmentPhase.Sctp).Status == EstablishmentStatus.Succeeded,
                "SCTP association did not establish.");
            await using var channels = new DataChannelAssociation(sctp);
            var accepting = First(channels.AcceptChannelsAsync(ct), ct);
            var local = await peer.OpenDataChannelAsync(new("events", "", true,
                DataChannelReliability.Reliable, 0, 256), ct);
            var remote = await accepting;
            Check(local.StreamId % 2 == (setup == SdpSetup.Active ? 1 : 0),
                "DCEP stream parity must follow DTLS, not SCTP role.");
            var receivingRemote = First(remote.ReceiveMessagesAsync(ct), ct);
            await local.SendTextAsync("local-to-remote", ct);
            Check((await receivingRemote).GetText() == "local-to-remote", "Remote data mismatch.");
            var receivingLocal = First(local.ReceiveMessagesAsync(ct), ct);
            await remote.SendTextAsync("remote-to-local", ct);
            Check((await receivingLocal).GetText() == "remote-to-local", "Owned data mismatch.");
            Console.WriteLine($"SCTP role regression passed: answer={setup}, remote={remoteRole}, DCEP stream={local.StreamId}, bidirectional data.");
        }
        finally
        {
            remoteLifetime.Cancel();
            await peer.DisposeAsync();
            // Observe both task outcomes before disposing the remote transport owners.
            try { await connecting; } catch (Exception) { }
            try { await remoteConnecting; }
            catch (OperationCanceledException) when (remoteLifetime.IsCancellationRequested) { }
            catch (IOException) when (baselineReproduced) { /* Owned timeout closed remote DTLS. */ }
        }
    }
}
