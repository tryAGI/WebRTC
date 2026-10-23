using tryAGI.WebRTC;

internal static class PacketAllocationChecks
{
    internal static readonly string[] ZeroOperations = ["rtp-mid", "rtp-truncated", "rtcp-framing", "stun-fingerprint", "stun-write", "stun-write-sha1", "stun-write-sha256", "stun-read-sha1", "stun-read-sha256", "stun-reject-sha1", "stun-reject-sha256", "stun-read-large-sha1", "stun-read-large-sha256", "stun-reject-large-sha1", "stun-reject-large-sha256"];
    internal static readonly string[] Operations = ZeroOperations;
    internal static byte[] Fixture(string operation) => operation switch
    {
        "rtp-mid" => Convert.FromHexString("906f00010000000200000003bede000110610000aabb"),
        "rtp-truncated" => Convert.FromHexString("906f00010000000200000003bede0002"),
        "rtcp-framing" => Convert.FromHexString("80c9000100000003"),
        "stun-fingerprint" => StunFixture(),
        "stun-write" or "stun-write-sha1" or "stun-write-sha256" => new byte[128],
        "stun-read-sha1" or "stun-reject-sha1" => StunFixture(true, false),
        "stun-read-sha256" or "stun-reject-sha256" => StunFixture(true, true),
        "stun-read-large-sha1" or "stun-reject-large-sha1" => StunFixture(true, false, 4096),
        "stun-read-large-sha256" or "stun-reject-large-sha256" => StunFixture(true, true, 4096),
        _ => throw new ArgumentException("Unknown packet operation", nameof(operation)),
    };
    private static byte[] StunFixture(bool authenticated = false, bool sha256 = false, int padding = 0)
    {
        var data = new byte[128 + padding];
        var writer = new StunMessageWriter(data, StunMessage.BindingRequest, "012345678901"u8);
        if (padding != 0 && !writer.TryAddAttribute(0x8022, new byte[padding])) throw new IOException("STUN padding fixture failed");
        if (!writer.TryAddAttribute(0x0006, "synthetic-peer"u8) || !writer.TryComplete(authenticated ? "synthetic-key"u8 : [], true, out var length, sha256))
            throw new IOException("STUN fixture could not be written");
        return data[..length];
    }
    internal static void Exercise(string operation, byte[] data, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var valid = operation switch
            {
                "rtp-mid" => RtpPacket.TryParse(data, out var packet) &&
                    RtpHeaderExtensions.TryValidateAndRead(packet, 1, out var mid, out var present) && present && mid.SequenceEqual("a"u8),
                "rtp-truncated" => !RtpPacket.TryParse(data, out _),
                "rtcp-framing" => RtcpFraming.IsValid(data),
                "stun-fingerprint" => StunMessage.TryParse(data, out var message) && message.VerifyFingerprint() &&
                    message.TryGetUniqueAttribute(0x0006, out var username) && username.SequenceEqual("synthetic-peer"u8),
                "stun-write" => Write(data, false, false),
                "stun-write-sha1" => Write(data, true, false),
                "stun-write-sha256" => Write(data, true, true),
                "stun-read-sha1" or "stun-read-large-sha1" => StunMessage.TryParse(data, out var sha1) && sha1.VerifyMessageIntegritySha1("synthetic-key"u8),
                "stun-read-sha256" or "stun-read-large-sha256" => StunMessage.TryParse(data, out var sha256) && sha256.VerifyMessageIntegritySha256("synthetic-key"u8),
                "stun-reject-sha1" or "stun-reject-large-sha1" => StunMessage.TryParse(data, out var bad1) && !bad1.VerifyMessageIntegritySha1("wrong-key"u8),
                "stun-reject-sha256" or "stun-reject-large-sha256" => StunMessage.TryParse(data, out var bad256) && !bad256.VerifyMessageIntegritySha256("wrong-key"u8),
                _ => false,
            };
            if (!valid) throw new IOException("Packet allocation workload failed its semantic assertion");
        }
    }
    private static bool Write(byte[] data, bool authenticated, bool sha256)
    {
        var writer = new StunMessageWriter(data, StunMessage.BindingRequest, "012345678901"u8);
        return writer.TryAddAttribute(0x0006, "synthetic-peer"u8) &&
            writer.TryAddUInt32(0x0024, 1234) &&
            writer.TryComplete(authenticated ? "synthetic-key"u8 : [], true, out var length, sha256) &&
            StunMessage.TryParse(data.AsSpan(0, length), out var message) && message.VerifyFingerprint();
    }
    internal static void AuthenticationBoundaries()
    {
        foreach (var sha256 in new[] { false, true })
        foreach (var length in new[] { 2024, 2028, 4096, 65484 })
        {
            var buffer = new byte[65552];
            var writer = new StunMessageWriter(buffer, StunMessage.BindingRequest, "012345678901"u8);
            if (!writer.TryAddAttribute(0x8022, new byte[length]) ||
                !writer.TryComplete("synthetic-key"u8, true, out var written, sha256))
                throw new IOException("STUN boundary fixture failed");
            var data = buffer[..written];
            var original = (byte[])data.Clone();
            if (!StunMessage.TryParse(data, out var message) || !message.VerifyFingerprint() ||
                !(sha256 ? message.VerifyMessageIntegritySha256("synthetic-key"u8) : message.VerifyMessageIntegritySha1("synthetic-key"u8)) ||
                (sha256 ? message.VerifyMessageIntegritySha256("wrong-key"u8) : message.VerifyMessageIntegritySha1("wrong-key"u8)) ||
                !data.AsSpan().SequenceEqual(original))
                throw new IOException("STUN boundary authentication mutated input or gave an incorrect result");
        }
    }
    internal static void ZeroAllocation()
    {
        foreach (var operation in ZeroOperations)
        {
            var data = Fixture(operation);
            Exercise(operation, data, 1024);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Exercise(operation, data, 4096);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated != 0) throw new IOException($"Warm {operation} allocated {allocated} bytes");
        }
        // Positive control: the counter must observe a deliberate escaping allocation.
        var start = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[256]);
        if (GC.GetAllocatedBytesForCurrentThread() - start < 256)
            throw new IOException("Allocation measuring control failed");
    }
}
