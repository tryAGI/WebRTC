using tryAGI.WebRTC;
using System.Net;
using System.Net.Sockets;

foreach (var scenario in new[] { "canonical-unordered", "canonical-ordered", "padded-ordered", "ordered-reject", "padded-reject", "unknown-reject", "remote-reject", "open-reject" })
    await DataChannelAckTests.Exchange(scenario);
Console.WriteLine("NativeAOT bounded DCEP ACK compatibility and negative controls passed");
await DtlsReceiveBoundsTests.IceBoundary();
await DtlsReceiveBoundsTests.Exchange(1200);
await DtlsReceiveBoundsTests.Exchange(2048);
Console.WriteLine("NativeAOT independent receive limits and atomic oversized-record rejection passed");
foreach (var stage in new[] { "dtls", "sctp", "dcep", "deadline" }) await EstablishmentTests.Stall(stage);
foreach (var setup in new[] { SdpSetup.Active, SdpSetup.Passive }) await EstablishmentTests.Repeated(setup);
foreach (var cancel in new[] { false, true }) await EstablishmentTests.IceFailure(cancel);
await EstablishmentTests.AuthenticationFailure();
await EstablishmentTests.Bounds();
Console.WriteLine("NativeAOT establishment failures, retained evidence, repeated peers and bounded history passed");
foreach (var setup in new[] { SdpSetup.Active, SdpSetup.Passive })
    foreach (var remoteRole in Enum.GetValues<SctpRole>()) await PeerSctpRoleTests.Exchange(setup, remoteRole);
Console.WriteLine("NativeAOT SCTP active/passive and simultaneous INIT in both DTLS roles passed");
Span<byte> binding = stackalloc byte[20];
if (!StunMessage.TryWriteBindingRequest(binding, "012345678901"u8) ||
    !StunMessage.TryParse(binding, out var stun) || stun.Type != StunMessage.BindingRequest)
{
    return 1;
}
var data = Convert.FromHexString("806f00010000000200000003aabb");
if (!RtpPacket.TryParse(data, out var rtp) || rtp.Payload.Length != 2)
{
    return 1;
}
SdpRtcpTests.Negotiation(); SdpRtcpTests.Bounds();
RtcpTests.Vectors(); RtcpTests.SdesAndBye(); RtcpTests.CompoundAndUnknown(); RtcpTests.BoundsAndCorpus();
RtcpTests.Reception(); RtcpTests.Clock(); RtcpTests.Schedule();
Console.WriteLine("NativeAOT bounded RTCP semantic codec, reception statistics and scheduling passed");
foreach (var profile in Enum.GetValues<SrtpProfile>()) await RtcpNetworkTests.Network(profile);
Console.WriteLine("NativeAOT independent RTCP vectors over encrypted ICE and replay rejection passed");
foreach (var profile in Enum.GetValues<SrtpProfile>())
    foreach (var setup in new[] { SdpSetup.Active, SdpSetup.Passive }) await RtcpPeerTests.Reports(profile, setup);
foreach (var reduced in new[] { false, true })
{
    await RtcpPeerTests.Feedback(reduced);
    await RtcpPeerTests.Feedback(reduced, delayedObservation: true);
    foreach (var pli in new[] { false, true }) await RtcpPeerTests.Admission(reduced, pli);
}
await RtcpPeerTests.RegularDeadline();
await RtcpPeerTests.BudgetAndCancellation();
Console.WriteLine("NativeAOT automatic peer RTCP reports, feedback, admission and bounded send queue passed");
VideoTests.H264(); VideoTests.Vp8(); VideoTests.LossAndDuplicates(); VideoTests.Malformed(); VideoTests.Routing(); VideoTests.BoundsAndExpiry(); VideoTests.LifetimeAndCorpus();
Console.WriteLine("NativeAOT bounded video H264/VP8 reassembly and hostile input passed");
SdpVideoTests.Selection(); SdpVideoTests.Profiles(); SdpVideoTests.ParametersAndBounds(); SdpVideoTests.HostileAnswer();
foreach (var codec in Enum.GetValues<VideoCodec>())
    foreach (var profile in Enum.GetValues<SrtpProfile>()) await VideoPeerTests.Local(codec, profile, SdpSetup.Active);
foreach (var transport in Enum.GetValues<TurnServerTransport>())
    foreach (var codec in Enum.GetValues<VideoCodec>())
        foreach (var profile in Enum.GetValues<SrtpProfile>())
        {
            await VideoPeerTests.Local(codec, profile, SdpSetup.Active, transport);
            Console.WriteLine($"NativeAOT owned video peer {codec} via {transport} {profile} passed");
        }
await VideoPeerTests.Direction();
Console.WriteLine("NativeAOT owned SDP video peer negotiation, queues and silence expiry passed");
foreach (var scenario in new[] { "valid", "duplicate", "bytes", "messages", "malformed", "unconfirmed-incoming", "further-reset" })
{
    await DataChannelGenerationTests.EarlyOpen(scenario);
    Console.WriteLine($"NativeAOT bounded DCEP early next generation {scenario} passed");
}
Console.WriteLine("NativeAOT protocol smoke passed");
await using var controlling = new IceUdpTransport(new(IPAddress.Loopback, 0));
await using var controlled = new IceUdpTransport(new(IPAddress.Loopback, 0));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await Task.WhenAll(
    controlling.ConnectAsync(controlled.LocalCredentials, IceRole.Controlling, [new(controlled.LocalEndPoint)], timeout.Token),
    controlled.ConnectAsync(controlling.LocalCredentials, IceRole.Controlled, [new(controlling.LocalEndPoint)], timeout.Token));
await controlling.SendDatagramAsync("native-ice"u8.ToArray(), timeout.Token);
if (!(await Read(controlled, timeout.Token)).AsSpan().SequenceEqual("native-ice"u8)) return 1;
Console.WriteLine("NativeAOT authenticated ICE network smoke passed");
foreach (var profile in Enum.GetValues<SrtpProfile>())
{
    var key = new byte[profile == SrtpProfile.AeadAes256Gcm ? 32 : 16];
    var salt = new byte[profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12];
    System.Security.Cryptography.RandomNumberGenerator.Fill(key);
    System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
    using var sender = new SrtpContext(profile, SrtpDirection.Send, key, salt);
    using var receiver = new SrtpContext(profile, SrtpDirection.Receive, key, salt);
    System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
    System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);
    var secure = new byte[data.Length + sender.RtpOverhead];
    if (!sender.TryProtectRtp(data, secure, out _)) return 1;
    await controlling.SendDatagramAsync(secure, timeout.Token);
    var packet = await Read(controlled, timeout.Token);
    var recovered = new byte[packet.Length];
    if (!receiver.TryUnprotectRtp(packet, recovered, out var length) || !recovered.AsSpan(0, length).SequenceEqual(data)) return 1;
    if (receiver.TryUnprotectRtp(packet, recovered, out _)) return 1;
    var control = Convert.FromHexString("80c9000100000003");
    secure = new byte[control.Length + sender.RtcpOverhead];
    if (!sender.TryProtectRtcp(control, secure, out _) || !receiver.TryUnprotectRtcp(secure, new byte[control.Length], out _)) return 1;
}
Console.WriteLine("NativeAOT SRTP/SRTCP profiles and encrypted ICE network smoke passed");
foreach (var profile in Enum.GetValues<SrtpProfile>())
foreach (var transport in Enum.GetValues<TurnServerTransport>())
{
    await using var relayServer = transport == TurnServerTransport.Udp ? new TurnFixture(modern: true) : null;
    await using var streamServer = transport == TurnServerTransport.Udp ? null : new TurnStreamFixture(transport == TurnServerTransport.Tls);
    var fixture = streamServer?.Backend ?? relayServer!;
    await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new() { RelayOnly = true });
    await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var localRelay = await left.GatherRelayCandidateAsync(streamServer?.Server ?? relayServer!.Server, new(TurnFixture.Username, TurnFixture.Secret), streamServer?.Options ?? TurnFixture.Fast(), deadline.Token);
    using var clientIdentity = DtlsIdentity.Generate(); using var serverIdentity = DtlsIdentity.Generate();
    var localOffer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusOffer(new(left.LocalCredentials, clientIdentity.GetFingerprintSha256(), left.LocalEndPoint, maximumMessageSize: 16384, additionalCandidates: [localRelay], relayOnly: true), 3));
    var localAnswer = SdpSessionDescription.Parse(SdpNegotiation.CreateOpusAnswer(localOffer, new(right.LocalCredentials, serverIdentity.GetFingerprintSha256(), right.LocalEndPoint, maximumMessageSize: 16384), 4, preferredSetup: SdpSetup.Passive));
    var localSession = SdpNegotiation.ValidateOpusAnswer(localOffer, localAnswer, true);
    var remoteSession = SdpNegotiation.ValidateOpusAnswer(localOffer, localAnswer, false);
    if (localSession.DtlsRole != DtlsRole.Client || remoteSession.DtlsRole != DtlsRole.Server || localSession.AudioCodec!.PayloadType != 111) return 1;
    await Task.WhenAll(left.ConnectAsync(localSession.RemoteCredentials, localSession.IceRole, localSession.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), deadline.Token),
        right.ConnectAsync(remoteSession.RemoteCredentials, remoteSession.IceRole, remoteSession.RemoteCandidates.Select(c => c.GetResolvedUdpCandidate()!), deadline.Token));
    var options = new DtlsSrtpOptions { Profiles = [profile], MaximumDatagramSize = 256 };
    await using var client = new DtlsSrtpTransport(left, clientIdentity, localSession.DtlsRole, Convert.FromHexString(localSession.RemoteFingerprintSha256), options);
    await using var server = new DtlsSrtpTransport(right, serverIdentity, remoteSession.DtlsRole, Convert.FromHexString(remoteSession.RemoteFingerprintSha256), options);
    await Task.WhenAll(client.ConnectAsync(deadline.Token), server.ConnectAsync(deadline.Token));
    await client.SendApplicationDatagramAsync("native-dtls"u8.ToArray(), deadline.Token);
    var receivedApp = false;
    await foreach (var packet in server.ReceiveApplicationDatagramsAsync(deadline.Token))
    { if (!packet.AsSpan().SequenceEqual("native-dtls"u8)) return 1; receivedApp = true; break; }
    if (!receivedApp) return 1;
    var encodedAudio = Convert.FromHexString("806F002A00017700000000037881A8B036089FC201D66EF7DFFADA025AF3F4969ED2892A0995E48742F90670483DAD77C7F0A9A749175731FD11D709FF8D7B1B5F70A9480AA33804");
    await client.SendRtpAsync(encodedAudio, deadline.Token);
    var receivedMedia = false;
    await foreach (var packet in server.ReceiveMediaDatagramsAsync(deadline.Token))
    { if (packet.Kind != SecureMediaKind.Rtp || !packet.Data.AsSpan().SequenceEqual(encodedAudio)) return 1; receivedMedia = true; break; }
    if (!receivedMedia) return 1;
    foreach (var codec in Enum.GetValues<VideoCodec>())
        await VideoNetworkTests.Exchange(client, server, codec, false, deadline.Token);
    Console.WriteLine($"NativeAOT authenticated video H264/VP8 via {transport} relay {profile} passed");
    var sctpOptions = new SctpOptions { MaximumPacketSize = 200, MaximumMessageSize = 16384, ReceiveBufferBytes = 16384, MaximumQueuedMessages = 1 };
    await using var outgoing = new SctpAssociation(client, SctpRole.Initiator, sctpOptions);
    await using var incoming = new SctpAssociation(server, SctpRole.Responder, sctpOptions);
    await Task.WhenAll(outgoing.ConnectAsync(deadline.Token), incoming.ConnectAsync(deadline.Token));
    if (!outgoing.SupportsPartialReliability || !incoming.SupportsPartialReliability) return 1;
    // Hold a complete maximum-size message, closing the receiver window. The
    // next timed message must expire, signal FORWARD-TSN and unblock later data.
    await outgoing.SendMessageAsync(1, 53, new byte[16384], cancellationToken: deadline.Token);
    await outgoing.DrainAsync(deadline.Token);
    await outgoing.SendMessageAsync(1, 53, new byte[8192], cancellationToken: deadline.Token,
        reliability: new(DataChannelReliability.Timed, 60));
    await outgoing.DrainAsync(deadline.Token);
    if (outgoing.GetDiagnostics().AbandonedMessages != 1) return 1;
    var held = false;
    await foreach (var message in incoming.ReceiveMessagesAsync(deadline.Token)) { if (message.Data.Length != 16384) return 1; held = true; break; }
    if (!held) return 1;
    await outgoing.SendMessageAsync(1, 51, "after-native-forward"u8.ToArray(), cancellationToken: deadline.Token);
    var afterForward = false;
    await foreach (var message in incoming.ReceiveMessagesAsync(deadline.Token)) { if (!message.Data.AsSpan().SequenceEqual("after-native-forward"u8)) return 1; afterForward = true; break; }
    if (!afterForward) return 1;
    await using var channels = new DataChannelAssociation(outgoing);
    await using var peerChannels = new DataChannelAssociation(incoming);
    var channel = await channels.OpenChannelAsync("oai-events", cancellationToken: deadline.Token);
    DataChannel? peer = null;
    await foreach (var opened in peerChannels.AcceptChannelsAsync(deadline.Token)) { peer = opened; break; }
    if (peer == null) return 1;
    var application = new byte[16384]; System.Security.Cryptography.RandomNumberGenerator.Fill(application);
    await channel.SendBinaryAsync(application, deadline.Token);
    var receivedChannel = false;
    await foreach (var message in peer.ReceiveMessagesAsync(deadline.Token))
    { if (!message.Data.AsSpan().SequenceEqual(application)) return 1; receivedChannel = true; break; }
    if (!receivedChannel) return 1;
    foreach (var reliability in new[] { DataChannelReliability.RetransmissionLimited, DataChannelReliability.Timed })
    {
        var partial = await channels.OpenChannelAsync(new("native-partial", "", reliability == DataChannelReliability.Timed,
            reliability, reliability == DataChannelReliability.Timed ? 3000u : 0u, 256), deadline.Token);
        DataChannel? accepted = null;
        await foreach (var opened in peerChannels.AcceptChannelsAsync(deadline.Token)) { accepted = opened; break; }
        if (accepted == null || accepted.Parameters.Reliability != reliability) return 1;
        await partial.SendBinaryAsync(application, deadline.Token);
        var gotPartial = false;
        await foreach (var message in accepted.ReceiveMessagesAsync(deadline.Token))
        { if (!message.Data.AsSpan().SequenceEqual(application)) return 1; gotPartial = true; break; }
        if (!gotPartial) return 1;
    }
    await channel.CloseAsync(deadline.Token);
    if (await channel.Completion != null || await peer.Completion.WaitAsync(deadline.Token) != null) return 1;
    var reused = await channels.OpenChannelAsync("native-reused", cancellationToken: deadline.Token);
    DataChannel? reusedPeer = null;
    await foreach (var opened in peerChannels.AcceptChannelsAsync(deadline.Token)) { reusedPeer = opened; break; }
    if (reusedPeer == null || reused.StreamId != channel.StreamId || ReferenceEquals(reusedPeer, peer)) return 1;
    await reused.SendTextAsync("after-native-reset", deadline.Token);
    var afterReset = false;
    await foreach (var message in reusedPeer.ReceiveMessagesAsync(deadline.Token))
    { if (message.GetText() != "after-native-reset") return 1; afterReset = true; break; }
    if (!afterReset) return 1;
    await reused.CloseAsync(deadline.Token);
    await outgoing.CloseAsync(deadline.Token);
    if (left.GetDiagnostics() is not { SelectedLocalCandidateType: IceCandidateType.Relay, LocalPaths: 1 }) return 1;
    await left.DisposeAsync(); if (fixture.Deletes != 1 || fixture.Allocations != 0) return 1;
}
Console.WriteLine("NativeAOT owned UDP/TCP/TLS relay ICE carries encrypted DTLS/SRTP/SCTP with all profiles and releases allocation");
Console.WriteLine("NativeAOT SDP-driven encoded Opus, fragmented DTLS, negotiated SRTP, SCTP/DCEP and PR-SCTP FORWARD-TSN and stream-reset/reuse passed");
foreach (var profile in Enum.GetValues<SrtpProfile>())
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
    var peerOptions = new PeerConnectionOptions { LocalEndPoint = new(IPAddress.Loopback, 0), Dtls = new() { Profiles = [profile] } };
    await using var local = new PeerConnection(peerOptions); await using var remote = new PeerConnection(peerOptions);
    using var stunServer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    stunServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var responding = NativeStun(stunServer, ct);
    var gathered = await local.GatherServerReflexiveCandidateAsync((IPEndPoint)stunServer.LocalEndPoint!, cancellationToken: ct);
    await responding;
    if (!gathered.EndPoint.Equals(local.GetLocalCandidates()[0].EndPoint) || local.GetLocalCandidates().Count != 1) return 1;
    local.CompleteGathering(); remote.CompleteGathering();
    local.SetRemoteAnswer(remote.CreateAnswer(local.CreateOffer()));
    await Task.WhenAll(local.ConnectAsync(ct), remote.ConnectAsync(ct));
    var channel = await local.OpenDataChannelAsync(new("native-owned", "", true, DataChannelReliability.Reliable, 0, 256), ct);
    DataChannel? accepted = null;
    await foreach (var value in remote.AcceptDataChannelsAsync(ct)) { accepted = value; break; }
    if (accepted == null) return 1;
    await channel.SendTextAsync("owned-native", ct);
    var controlReceived = false;
    await foreach (var message in accepted.ReceiveMessagesAsync(ct)) { if (message.GetText() != "owned-native") return 1; controlReceived = true; break; }
    if (!controlReceived) return 1;
    var payload = Convert.FromHexString("7881A8B036089FC201D66EF7DFFADA025AF3F4969ED2892A0995E48742F90670483DAD77C7F0A9A749175731FD11D709FF8D7B1B5F70A9480AA33804");
    await local.SendOpusAsync(payload, 96000, cancellationToken: ct);
    var audioReceived = false;
    await foreach (var audio in remote.ReceiveAudioAsync(ct))
    { if (audio.Timestamp != 96000 || audio.SynchronizationSource != local.AudioSource || !audio.Payload.AsSpan().SequenceEqual(payload)) return 1; audioReceived = true; break; }
    if (!audioReceived) return 1;
    await channel.CloseAsync(ct); await local.CloseAsync(ct);
    if (await remote.Completion.WaitAsync(ct) != null) return 1;
}
Console.WriteLine("NativeAOT owned peer Opus/data lifecycle and all SRTP profiles passed");
Console.WriteLine("NativeAOT same-socket STUN gathering and explicit SDP completion passed");
await TurnFixture.RoundTrip(modern:true,channel:true);
foreach (var tls in new[] { false, true })
    foreach (var channel in new[] { false, true }) await TurnStreamTests.RoundTrip(tls, channel);
Console.WriteLine("NativeAOT TURN TCP/TLS framing, validated TLS identity, encrypted relay ICE and deletion passed");
Console.WriteLine("NativeAOT owned TURN allocation, SHA256 auth, ChannelData, renewal and deletion passed");
IceServerUriTests.Vectors(); IceServerUriTests.Malformed(); IceServerUriTests.Corpus();
foreach (var ipv6 in new[] { false, true })
    foreach (var dns in new[] { false, true }) await IceServerTests.Stun(ipv6, dns);
await IceServerTests.Policy(); await IceServerTests.Admission();
foreach (var scenario in new[] { "cancel", "timeout", "request-timeout", "dispose" }) await IceServerTests.Lifetime(scenario);
foreach (var transport in Enum.GetValues<TurnServerTransport>()) await IceServerTests.Relay(transport);
await IceServerTests.Relay(TurnServerTransport.Tls, true);
Console.WriteLine("NativeAOT ICE server URI/DNS, destination policy, original TLS identity and bounded gathering lifetime passed");
await DiagnosticTests.Stages(null);
foreach (var transport in Enum.GetValues<TurnServerTransport>()) await DiagnosticTests.Stages(transport);
await DiagnosticTests.QueueAndLifecycle(); await DiagnosticTests.CollectorIsolation(false); await DiagnosticTests.CollectorIsolation(true);
await DiagnosticTests.CallerPacing(); await DiagnosticTests.SamplingAndActivities(); await DiagnosticTests.ReceptionScopes(); await DiagnosticBoundaryTests.ManagedHandlerStall(); await DiagnosticBoundaryTests.SecureProcessingWait(); await DiagnosticBoundaryTests.NetworkFaults();
Console.WriteLine("NativeAOT opt-in diagnostics, UDP/TCP/TLS stages, collector isolation and controlled fault boundaries passed");
return 0;

static async Task NativeStun(Socket server, CancellationToken ct)
{
    var request = new byte[128]; var received = await server.ReceiveFromAsync(request, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
    if (!StunMessage.TryParse(request.AsSpan(0, received.ReceivedBytes), out var message) || !message.VerifyFingerprint() ||
        message.Type != StunMessage.BindingRequest) throw new IOException("Invalid native STUN gather request.");
    var response = new byte[128]; var writer = new StunMessageWriter(response, 0x0101, message.TransactionId);
    if (!writer.TryAddXorMappedAddress((IPEndPoint)received.RemoteEndPoint) || !writer.TryComplete([], true, out var length))
        throw new IOException("Native STUN response framing failed.");
    await server.SendToAsync(response.AsMemory(0, length), SocketFlags.None, received.RemoteEndPoint, ct);
}

static async Task<byte[]> Read(IceUdpTransport transport, CancellationToken cancellationToken)
{
    await foreach (var packet in transport.ReceiveDatagramsAsync(cancellationToken)) return packet;
    throw new IOException("ICE transport ended without receiving the required smoke packet.");
}
