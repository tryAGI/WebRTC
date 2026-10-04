using System.Buffers.Binary;
using System.Net;
using System.Text;
using tryAGI.WebRTC;

var tests = new (string Name, Action Run)[]
{
    ("ICE server URI RFC7064/7065 vectors and canonical transport", IceServerUriTests.Vectors),
    ("ICE server URI malformed, ambiguous and restricted addresses", IceServerUriTests.Malformed),
    ("ICE server URI bounds and hostile corpus", IceServerUriTests.Corpus),
    ("RTCP SDP PLI, reduced-size and semantic feedback subset", SdpRtcpTests.Negotiation),
    ("RTCP SDP hostile attributes, immutable models and bounds", SdpRtcpTests.Bounds),
    ("RTCP independent signed report, Unicode CNAME and PLI vectors", RtcpTests.Vectors),
    ("RTCP SDES/PRIV/BYE boundaries and immutable views", RtcpTests.SdesAndBye),
    ("RTCP compound identity and opaque unsupported feedback", RtcpTests.CompoundAndUnknown),
    ("RTCP byte/packet/item/count bounds and hostile corpus", RtcpTests.BoundsAndCorpus),
    ("RTCP reception loss/duplicate/reorder/restart/jitter statistics", RtcpTests.Reception),
    ("RTCP NTP fraction and era rollover", RtcpTests.Clock),
    ("RTCP endpoint scheduling, early coalescing and topology changes", RtcpTests.Schedule),
    ("Video SDP codec selection, directions and DTLS roles", SdpVideoTests.Selection),
    ("Video SDP equivalent profiles, Level1b, defaults and asymmetry", SdpVideoTests.Profiles),
    ("Video SDP sender parameter sets and capability bounds", SdpVideoTests.ParametersAndBounds),
    ("Video SDP hostile answer and payload collision preflight", SdpVideoTests.HostileAnswer),
    ("Video H264 STAP/FU mode bounds and sequence/timestamp rollover", VideoTests.H264),
    ("Video VP8 descriptor extensions and partition reconstruction", VideoTests.Vp8),
    ("Video loss, late packets, duplicates and access-unit boundaries", VideoTests.LossAndDuplicates),
    ("Video malformed aggregation/fragment/descriptor identity", VideoTests.Malformed),
    ("Video authorized source, payload and MID routing", VideoTests.Routing),
    ("Video frame/packet/expanded-byte/age budgets", VideoTests.BoundsAndExpiry),
    ("Video restart/disposal and bounded hostile-input corpus", VideoTests.LifetimeAndCorpus),
    ("TURN/STUN independent SHA256 integrity vector and tampering", TurnTests.Integrity),
    ("TURN/STUN SHA256 framing/capacity/duplicate bounds", TurnTests.Bounds),
    ("TURN mapped/relayed/peer IPv4/IPv6 address framing", TurnTests.Addresses),
    ("RFC8285 one/two-byte MID framing and hostile elements", MediaTests.Extensions),
    ("RTCP compound/reduced-size framing and report bounds", MediaTests.Control),
    ("SDP local relay provenance and relay-only inventory", SdpTests.RelayCandidates),
    ("SDP bounded syntax, inheritance and immutable models", SdpTests.Syntax),
    ("SDP duplicate/security/framing/size rejection", SdpTests.Malformed),
    ("SDP full bounded RTP payload inventory and selection", SdpTests.FullPayloadInventory),
    ("SDP broad browser codecs, feedback, attribute bounds and selection", SdpTests.BrowserCodecInventory),
    ("SDP DTLS/ICE roles and effective media directions", SdpTests.RolesAndDirections),
    ("SDP answer negotiation before transport side effects", SdpTests.NegotiationPreflight),
    ("SDP deterministic hostile-input corpus", SdpTests.HostileCorpus),
    ("SCTP independent CRC32C and chunk framing", SctpTests.Framing),
    ("SCTP malformed framing and resource bounds", SctpTests.Bounds),
    ("DCEP wire types, Unicode and reliable parameter semantics", SctpTests.Dcep),
    ("DCEP malformed and bounded hostile inputs", SctpTests.DcepMalformed),
    ("STUN binding header and overlapping buffers", BindingHeader),
    ("STUN header and attribute boundaries", StunBoundaries),
    ("STUN independent authentication vector", AuthenticationVector),
    ("STUN authentication rejects changed data and keys", AuthenticationTampering),
    ("STUN rejects duplicate and misplaced authentication attributes", AuthenticationDuplicates),
    ("STUN IPv4 and IPv6 XOR address decoding", MappedAddresses),
    ("RTP fields and contributing sources", RtpFields),
    ("RTP extension and padding boundaries", RtpExtensions),
    ("RTP malformed datagrams", RtpMalformed),
    ("STUN and RTP deterministic malformed-input corpus", MalformedCorpus),
    ("Default views are safe", DefaultViews),
    ("STUN writer matches independently generated vector", WriterReference),
    ("STUN writer capacity and completion bounds", WriterBounds),
    ("ICE credentials and candidates reject unsafe input", IceInputBounds),
    ("SRTP/SRTCP independent Pion synthetic vectors and E=0 tampering", SrtpTests.IndependentVectors),
    ("SRTP/SRTCP profiles, padding, extensions and empty payload", SrtpTests.RoundTrips),
    ("SRTP/SRTCP every-byte tampering, truncation and wrong keys", SrtpTests.Tampering),
    ("SRTP rollover and authenticated out-of-order packets", SrtpTests.RolloverAndReordering),
    ("SRTP/SRTCP bounded replay window", SrtpTests.ReplayWindow),
    ("SRTP/SRTCP source bounds and unauthenticated state admission", SrtpTests.ResourceBounds),
    ("SRTP/SRTCP buffer bounds and overlap rejection", SrtpTests.BufferBounds),
    ("SRTP disposal and direction", SrtpTests.LifetimeAndDirection),
    ("SRTP/SRTCP deterministic hostile-input corpus", SrtpTests.MalformedInputs),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} protocol cases passed");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] IntegrityVector() => Convert.FromHexString(
    "0001003c2112a442000102030405060708090a0b" +
    "0006000f6c6f63616c3a706565722d7465737400" +
    "0024000412345678" +
    "00080014e927fce274b91580a98d8bb32a0e5d4089bd1b51" +
    "8028000425c95d1b");

static void BindingHeader()
{
    var buffer = new byte[24];
    var id = Convert.FromHexString("000102030405060708090a0b");
    Check(StunMessage.TryWriteBindingRequest(buffer, id));
    Check(StunMessage.TryParse(buffer.AsSpan(0, 20), out var message));
    Check(message.Type == StunMessage.BindingRequest && message.TransactionId.SequenceEqual(id));
    Check(!StunMessage.TryWriteBindingRequest(buffer.AsSpan(0, 19), id));
    Check(!StunMessage.TryWriteBindingRequest(buffer, id.AsSpan(0, 11)));
    id.CopyTo(buffer, 8);
    Check(StunMessage.TryWriteBindingRequest(buffer, buffer.AsSpan(8, 12)));
    Check(buffer.AsSpan(8, 12).SequenceEqual(id));
}

static void StunBoundaries()
{
    var data = IntegrityVector();
    for (var length = 0; length < data.Length; length++)
    {
        Check(!StunMessage.TryParse(data.AsSpan(0, length), out _), $"STUN truncation {length}");
    }
    Check(!StunMessage.TryParse([.. data, 0], out _));
    var changed = (byte[])data.Clone();
    changed[0] = 0xC0;
    Check(!StunMessage.TryParse(changed, out _));
    changed = (byte[])data.Clone();
    changed[4] ^= 1;
    Check(!StunMessage.TryParse(changed, out _));
    changed = (byte[])data.Clone();
    changed[3] |= 1;
    Check(!StunMessage.TryParse(changed, out _));
    changed = (byte[])data.Clone();
    changed[22] = changed[23] = 0xFF;
    Check(!StunMessage.TryParse(changed, out _));
    Check(StunMessage.TryParse(data, out var message));
    var attributes = message.GetAttributes();
    var count = 0;
    while (attributes.MoveNext()) count++;
    Check(count == 4);
    Check(message.TryGetUniqueAttribute(6, out var username));
    Check(username.SequenceEqual("local:peer-test"u8));
    Check(!message.TryGetUniqueAttribute(0xFFFF, out _));
}

static void AuthenticationVector()
{
    // Synthetic vector computed independently with Python hmac/hashlib/binascii.
    var data = IntegrityVector();
    Check(StunMessage.TryParse(data, out var message));
    Check(message.VerifyMessageIntegritySha1("local-test-password"u8));
    Check(message.VerifyFingerprint());
    // Padding is not required to be zero; when changed it still parses, but it
    // participates in the HMAC and CRC and therefore cannot retain authentication.
    data[39] = 0xAB;
    Check(StunMessage.TryParse(data, out message));
    Check(!message.VerifyMessageIntegritySha1("local-test-password"u8));
    Check(!message.VerifyFingerprint());
}

static void AuthenticationTampering()
{
    var data = IntegrityVector();
    Check(StunMessage.TryParse(data, out var message));
    Check(!message.VerifyMessageIntegritySha1("wrong"u8));
    Check(!message.VerifyMessageIntegritySha1([]));
    foreach (var index in new[] { 8, 24, 44, 52, 71 })
    {
        var changed = (byte[])data.Clone();
        changed[index] ^= 1;
        Check(StunMessage.TryParse(changed, out message));
        Check(!message.VerifyMessageIntegritySha1("local-test-password"u8), $"STUN HMAC byte {index}");
        Check(!message.VerifyFingerprint(), $"STUN CRC byte {index}");
    }
    data[^1] ^= 1;
    Check(StunMessage.TryParse(data, out message));
    Check(!message.VerifyFingerprint());
    // HMAC does not cover FINGERPRINT. Both checks are separate contracts.
    Check(message.VerifyMessageIntegritySha1("local-test-password"u8));
}

static void AuthenticationDuplicates()
{
    var original = IntegrityVector();
    var duplicate = new byte[original.Length + 24];
    original.CopyTo(duplicate, 0);
    original.AsSpan(48, 24).CopyTo(duplicate.AsSpan(original.Length));
    BinaryPrimitives.WriteUInt16BigEndian(duplicate.AsSpan(2), (ushort)(duplicate.Length - 20));
    Check(StunMessage.TryParse(duplicate, out var message));
    Check(!message.TryGetUniqueAttribute(StunMessage.MessageIntegrity, out _));
    Check(!message.VerifyMessageIntegritySha1("local-test-password"u8));
    Check(!message.VerifyFingerprint());

    var misplaced = new byte[original.Length + 4];
    original.CopyTo(misplaced, 0);
    BinaryPrimitives.WriteUInt16BigEndian(misplaced.AsSpan(2), (ushort)(misplaced.Length - 20));
    Check(StunMessage.TryParse(misplaced, out message));
    Check(!message.VerifyMessageIntegritySha1("local-test-password"u8));
    Check(!message.VerifyFingerprint());
}

static void MappedAddresses()
{
    foreach (var address in new[] { IPAddress.Parse("192.0.2.19"), IPAddress.Parse("2001:db8::1234") })
    {
        var raw = address.GetAddressBytes();
        var packet = new byte[28 + raw.Length];
        Check(StunMessage.TryWriteBindingRequest(packet, Convert.FromHexString("000102030405060708090a0b")));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length - 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), StunMessage.XorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)(raw.Length + 4));
        packet[25] = (byte)(raw.Length == 4 ? 1 : 2);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26), (ushort)(45678 ^ 0x2112));
        for (var i = 0; i < raw.Length; i++) packet[28 + i] = (byte)(raw[i] ^ packet[4 + i]);
        Check(StunMessage.TryParse(packet, out var message));
        Check(message.TryGetXorMappedEndpoint(out var endpoint));
        Check(endpoint!.Address.Equals(address) && endpoint.Port == 45678);
        packet[25] = 3;
        Check(StunMessage.TryParse(packet, out message));
        Check(!message.TryGetXorMappedEndpoint(out _));
    }
}

static void RtpFields()
{
    var data = Convert.FromHexString("82ef123401020304556677880000000100000002aabb");
    Check(RtpPacket.TryParse(data, out var packet));
    Check(packet.Marker && packet.PayloadType == 111);
    Check(packet.SequenceNumber == 0x1234 && packet.Timestamp == 0x01020304);
    Check(packet.SynchronizationSource == 0x55667788 && packet.ContributingSourceCount == 2);
    Check(packet.TryGetContributingSource(0, out var first) && first == 1);
    Check(packet.TryGetContributingSource(1, out var second) && second == 2);
    Check(!packet.TryGetContributingSource(-1, out _) && !packet.TryGetContributingSource(2, out _));
    Check(packet.Payload.SequenceEqual(new byte[] { 0xAA, 0xBB }));
    Check(!packet.HasExtension);
}

static void RtpExtensions()
{
    var data = Convert.FromHexString("b06f00010000000200000003bede0001aabbccdd10200002");
    Check(RtpPacket.TryParse(data, out var packet));
    Check(packet.HasExtension && packet.ExtensionProfile == 0xBEDE);
    Check(packet.ExtensionData.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));
    Check(packet.Payload.SequenceEqual(new byte[] { 0x10, 0x20 }));
    var changed = (byte[])data.Clone();
    changed[14] = changed[15] = 0xFF;
    Check(!RtpPacket.TryParse(changed, out _));
    changed = (byte[])data.Clone();
    changed[^1] = 0;
    Check(!RtpPacket.TryParse(changed, out _));
    changed[^1] = 5;
    Check(!RtpPacket.TryParse(changed, out _));
}

static void RtpMalformed()
{
    for (var length = 0; length < 12; length++)
    {
        var data = new byte[length];
        if (length > 0) data[0] = 0x80;
        Check(!RtpPacket.TryParse(data, out _));
    }
    Check(!RtpPacket.TryParse(Convert.FromHexString("c00000000000000000000000"), out _));
    Check(!RtpPacket.TryParse(Convert.FromHexString("810000000000000000000000"), out _));
    Check(!RtpPacket.TryParse(Convert.FromHexString("900000000000000000000000"), out _));
    Check(!RtpPacket.TryParse(Convert.FromHexString("a00000000000000000000000"), out _));
}

static void MalformedCorpus()
{
    var random = new Random(4719);
    for (var i = 0; i < 10000; i++)
    {
        var data = new byte[random.Next(0, 512)];
        random.NextBytes(data);
        // Force half the inputs through STUN header checks so random testing also
        // exercises hostile attribute lengths, rather than only rejecting cookies.
        if (i % 2 == 0 && data.Length >= 20)
        {
            data = data.AsSpan(0, 20 + ((data.Length - 20) & ~3)).ToArray();
            data[0] &= 0x3F;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)(data.Length - 20));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), StunMessage.MagicCookie);
        }
        if (StunMessage.TryParse(data, out var stun))
        {
            _ = stun.VerifyFingerprint();
            _ = stun.VerifyMessageIntegritySha1("synthetic-key"u8);
            _ = stun.TryGetXorMappedEndpoint(out _);
            var attributes = stun.GetAttributes();
            while (attributes.MoveNext()) Check(attributes.Value.Length <= data.Length);
        }
        if (RtpPacket.TryParse(data, out var rtp))
        {
            Check(rtp.Payload.Length <= data.Length && rtp.ExtensionData.Length <= data.Length);
            for (var index = 0; index < rtp.ContributingSourceCount; index++)
                Check(rtp.TryGetContributingSource(index, out _));
        }
    }
}

static void DefaultViews()
{
    StunMessage stun = default;
    Check(stun.Data.IsEmpty && stun.TransactionId.IsEmpty && stun.Type == 0);
    Check(!stun.VerifyFingerprint() && !stun.VerifyMessageIntegritySha1("key"u8));
    Check(!stun.TryGetXorMappedEndpoint(out _));
    RtpPacket rtp = default;
    Check(rtp.Payload.IsEmpty && rtp.ExtensionData.IsEmpty && !rtp.Marker);
    Check(!rtp.TryGetContributingSource(0, out _));
}

static void WriterReference()
{
    var expected = IntegrityVector();
    var buffer = new byte[expected.Length];
    var writer = new StunMessageWriter(buffer, StunMessage.BindingRequest, expected.AsSpan(8, 12));
    Check(writer.TryAddAttribute(6, "local:peer-test"u8));
    Check(writer.TryAddUInt32(0x24, 0x12345678));
    Check(writer.TryComplete("local-test-password"u8, true, out var length));
    Check(length == expected.Length && buffer.SequenceEqual(expected));
    Check(!writer.TryAddAttribute(1, []) && !writer.TryComplete([], true, out _));
    foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
    {
        var addressBuffer = new byte[128];
        var addressWriter = new StunMessageWriter(addressBuffer, 0x0101, expected.AsSpan(8, 12));
        Check(addressWriter.TryAddXorMappedAddress(new IPEndPoint(ip, 54321)));
        Check(addressWriter.TryComplete("local-test-password"u8, true, out length));
        Check(StunMessage.TryParse(addressBuffer.AsSpan(0, length), out var message));
        Check(message.VerifyFingerprint() && message.VerifyMessageIntegritySha1("local-test-password"u8));
        Check(message.TryGetXorMappedEndpoint(out var endpoint) && endpoint!.Equals(new IPEndPoint(ip, 54321)));
    }
}

static void WriterBounds()
{
    StunMessageWriter uninitialized = default;
    Check(!uninitialized.TryComplete([], false, out _) && !uninitialized.TryAddAttribute(1, []));
    Check(!uninitialized.TryAddXorMappedAddress(new(IPAddress.Loopback, 12345)));
    var buffer = new byte[48];
    var writer = new StunMessageWriter(buffer, 1, new byte[12]);
    Check(!writer.TryAddAttribute(StunMessage.MessageIntegrity, new byte[20]));
    Check(!writer.TryAddAttribute(StunMessage.Fingerprint, new byte[4]));
    Check(writer.TryAddUInt64(0x802A, 42));
    var before = (byte[])buffer.Clone();
    Check(!writer.TryComplete("key"u8, true, out _));
    Check(buffer.SequenceEqual(before));
    Check(writer.TryComplete([], true, out var length) && length == 40);
    Check(StunMessage.TryParse(buffer.AsSpan(0, length), out var message) && message.VerifyFingerprint());
    var maximum = new byte[65560];
    var largeWriter = new StunMessageWriter(maximum, 1, new byte[12]);
    Check(!largeWriter.TryAddAttribute(0x100, new byte[65535]));
    Check(largeWriter.TryAddAttribute(0x100, new byte[65520]));
    Check(!largeWriter.TryComplete("key"u8, true, out _));
    Check(largeWriter.TryComplete([], true, out length));
    Check(StunMessage.TryParse(maximum.AsSpan(0, length), out message) && message.VerifyFingerprint());
}

static void IceInputBounds()
{
    var ipv6Candidate = new IceCandidate(new(IPAddress.IPv6Loopback, 12345));
    ipv6Candidate.EndPoint.Address.ScopeId = 42;
    Check(ipv6Candidate.EndPoint.Address.ScopeId == 0);
    var credentials = IceCredentials.Generate();
    Check(!credentials.ToString().Contains(credentials.Password, StringComparison.Ordinal));
    foreach (var invalid in new[] { "", "short", new string('a', 257), new string('a', 21) + "-" })
    {
        try { _ = new IceCredentials("local", invalid); Check(false); }
        catch (ArgumentException) { }
    }
    foreach (var ip in new[] { IPAddress.Any, IPAddress.IPv6Any, IPAddress.Broadcast, IPAddress.Parse("224.0.0.1"), IPAddress.Parse("ff02::1") })
    {
        try { _ = new IceCandidate(new IPEndPoint(ip, 1234)); Check(false); }
        catch (ArgumentException) { }
    }
}
