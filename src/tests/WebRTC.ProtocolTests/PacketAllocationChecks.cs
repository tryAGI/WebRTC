using tryAGI.WebRTC;

internal static class PacketAllocationChecks
{
    internal static byte[] Fixture(string operation) => operation switch
    {
        "rtp-mid" => Convert.FromHexString("906f00010000000200000003bede000110610000aabb"),
        "rtp-truncated" => Convert.FromHexString("906f00010000000200000003bede0002"),
        "rtcp-framing" => Convert.FromHexString("80c9000100000003"),
        "stun-fingerprint" => StunFixture(),
        _ => throw new ArgumentException("Unknown packet operation", nameof(operation)),
    };
    private static byte[] StunFixture()
    {
        var data = new byte[128];
        var writer = new StunMessageWriter(data, StunMessage.BindingRequest, "012345678901"u8);
        if (!writer.TryAddAttribute(0x0006, "synthetic-peer"u8) || !writer.TryComplete([], true, out var length))
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
                _ => false,
            };
            if (!valid) throw new IOException("Packet allocation workload failed its semantic assertion");
        }
    }
    internal static void ZeroAllocation()
    {
        foreach (var operation in new[] { "rtp-mid", "rtp-truncated", "rtcp-framing", "stun-fingerprint" })
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
