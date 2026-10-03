using System.Buffers.Binary;
using tryAGI.WebRTC;

internal static class SrtpTests
{
    internal static readonly SrtpProfile[] Profiles = Enum.GetValues<SrtpProfile>();
    internal static byte[] Key(SrtpProfile profile) => Enumerable.Range(1, profile == SrtpProfile.AeadAes256Gcm ? 32 : 16).Select(i => (byte)i).ToArray();
    internal static byte[] Salt(SrtpProfile profile) => Enumerable.Range(61, profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12).Select(i => (byte)i).ToArray();
    private static SrtpContext Create(SrtpProfile profile, SrtpDirection direction) => new(profile, direction, Key(profile), Salt(profile));
    private static void Check(bool condition, string message = "SRTP assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        var caught = false;
        try { action(); } catch (T) { caught = true; }
        Check(caught, $"Expected {typeof(T).Name}");
    }

    internal static byte[] Rtp(ushort sequence, uint source = 0x21324354) =>
        Convert.FromHexString($"806f{sequence:x4}12345678{source:x8}010203040506070809");
    internal static byte[] Rtcp(uint source = 0x21324354) =>
        Convert.FromHexString($"80c80006{source:x8}1234567812345678876543210000000400000005");
    private static byte[] Protect(SrtpContext sender, byte[] packet, bool rtcp = false)
    {
        var output = new byte[packet.Length + sender.RtcpOverhead];
        int length;
        Check(rtcp ? sender.TryProtectRtcp(packet, output, out length) : sender.TryProtectRtp(packet, output, out length));
        return output[..length];
    }
    private static void Recover(SrtpContext receiver, byte[] encrypted, byte[] expected, bool rtcp = false)
    {
        var output = new byte[encrypted.Length];
        int length;
        Check(rtcp ? receiver.TryUnprotectRtcp(encrypted, output, out length) : receiver.TryUnprotectRtp(encrypted, output, out length));
        Check(output.AsSpan(0, length).SequenceEqual(expected));
    }
    private static void Reject(SrtpContext receiver, byte[] encrypted, bool rtcp = false)
    {
        var output = Enumerable.Repeat((byte)0xAB, encrypted.Length + 8).ToArray();
        int length;
        Check(!(rtcp ? receiver.TryUnprotectRtcp(encrypted, output, out length) : receiver.TryUnprotectRtp(encrypted, output, out length)));
        Check(length == 0 && output.All(b => b == 0xAB), "Rejected packet changed output");
    }

    // Generated from our public synthetic inputs by Pion SRTP v3.1.3,
    // commit 508d3e9955121cfe30d7e15791d5810d1290c438. Reproduce with
    // pion-peer --srtp-vectors; no upstream fixture/source is copied.
    internal static void IndependentVectors()
    {
        var vectors = new (SrtpProfile Profile, string Rtp, string Rtcp, string AuthOnly)[]
        {
            ((SrtpProfile)1, "806f00011234567821324354cfc8dccda949b1104fa361852dbca7d465f4ea",
                "80c80006213243540714e21340554dac8034da9b04c8d4ca3da7471e80000001d20a845245ff05648b1c",
                "80c80006213243541234567812345678876543210000000400000005000000017f5adaa5ac214fa402ad"),
            ((SrtpProfile)7, "806f000112345678213243545ddda649e369ed7938038136f0109cacb5a856b64286cc8683",
                "80c8000621324354560865a79bf09bbfef3a7bba8c7d02e444c49a8ae480e42f0771b530f13948b98c5b38ef80000001",
                "80c80006213243541234567812345678876543210000000400000005a2fe68080089233af594a363ca9b612000000001"),
            ((SrtpProfile)8, "806f00011234567821324354fe60bd6d98fc233ffcc60370f04bdfe7d121bf16935f7ecd8b",
                "80c80006213243542f663b8b1df4a5062561be23596200e2d2362273efacaa183736466f571cdcaaa06a126380000001",
                "80c800062132435412345678123456788765432100000004000000055bd049c25e726158ec5db98f2cd440f300000001"),
        };
        foreach (var (profile, rtp, rtcp, authOnly) in vectors)
        {
            using var sender = Create(profile, SrtpDirection.Send);
            using var receiver = Create(profile, SrtpDirection.Receive);
            Check(Protect(sender, Rtp(1)).SequenceEqual(Convert.FromHexString(rtp)), "Independent SRTP vector mismatch");
            Check(Protect(sender, Rtcp(), true).SequenceEqual(Convert.FromHexString(rtcp)), "Independent SRTCP vector mismatch");
            Recover(receiver, Convert.FromHexString(rtp), Rtp(1));
            Recover(receiver, Convert.FromHexString(rtcp), Rtcp(), true);
            using var authReceiver = Create(profile, SrtpDirection.Receive);
            var authenticated = Convert.FromHexString(authOnly);
            for (var i = 0; i < authenticated.Length; i++)
            {
                var mutated = (byte[])authenticated.Clone(); mutated[i] ^= 1;
                Reject(authReceiver, mutated, true);
            }
            Recover(authReceiver, authenticated, Rtcp(), true);
            Reject(authReceiver, authenticated, true);
        }
    }

    internal static void RoundTrips()
    {
        foreach (var profile in Profiles)
        {
            using var sender = Create(profile, SrtpDirection.Send);
            using var receiver = Create(profile, SrtpDirection.Receive);
            foreach (var packet in new[] { Rtp(1), Convert.FromHexString("b26f000212345678213243540000000100000002bede000110aa20bb11223304040404"), Rtp(3)[..12] })
                Recover(receiver, Protect(sender, packet), packet);
            foreach (var packet in new[] { Rtcp(), Convert.FromHexString("80c9000121324354"), Convert.FromHexString("80c900012132435480ca000100000000"), Convert.FromHexString("a0c900022132435400000004") })
                Recover(receiver, Protect(sender, packet, true), packet, true);
        }
    }

    internal static void Tampering()
    {
        foreach (var profile in Profiles)
        foreach (var rtcp in new[] { false, true })
        {
            using var sender = Create(profile, SrtpDirection.Send);
            var plain = rtcp ? Rtcp() : Rtp(65535);
            var packet = Protect(sender, plain, rtcp);
            using var receiver = Create(profile, SrtpDirection.Receive);
            for (var i = 0; i < packet.Length; i++)
            {
                var bad = (byte[])packet.Clone(); bad[i] ^= 1;
                Reject(receiver, bad, rtcp);
            }
            for (var length = 0; length < packet.Length; length++) Reject(receiver, packet[..length], rtcp);
            var key = Key(profile); key[0] ^= 1;
            using var wrongKey = new SrtpContext(profile, SrtpDirection.Receive, key, Salt(profile));
            Reject(wrongKey, packet, rtcp);
            var salt = Salt(profile); salt[0] ^= 1;
            using var wrongSalt = new SrtpContext(profile, SrtpDirection.Receive, Key(profile), salt);
            Reject(wrongSalt, packet, rtcp);
            Recover(receiver, packet, plain, rtcp);
            Reject(receiver, packet, rtcp);
        }
    }

    internal static void RolloverAndReordering()
    {
        foreach (var profile in Profiles)
        {
            using var sender = Create(profile, SrtpDirection.Send);
            using var receiver = Create(profile, SrtpDirection.Receive);
            var sequences = new ushort[] { 65533, 65534, 65535, 0, 1, 2 };
            var packets = sequences.Select(n => Protect(sender, Rtp(n))).ToArray();
            foreach (var i in new[] { 0, 3, 2, 1, 5, 4 }) Recover(receiver, packets[i], Rtp(sequences[i]));
            foreach (var packet in packets) Reject(receiver, packet);
            Check(!sender.TryProtectRtp(Rtp(2), new byte[128], out _), "Sender reused index");
            var differentPayload = Rtp(2); differentPayload[^1] ^= 1;
            Check(!sender.TryProtectRtp(differentPayload, new byte[128], out _), "Sender reused nonce for different payload");
            Check(!sender.TryProtectRtp(Rtp(65535), new byte[128], out _), "Sender reused old ROC");
            var next = Protect(sender, Rtp(3));
            Recover(receiver, next, Rtp(3));
        }
    }

    internal static void ReplayWindow()
    {
        foreach (var profile in Profiles)
        foreach (var rtcp in new[] { false, true })
        {
            using var sender = Create(profile, SrtpDirection.Send);
            using var receiver = Create(profile, SrtpDirection.Receive);
            var packets = Enumerable.Range(0, 70).Select(n => Protect(sender, rtcp ? Rtcp() : Rtp((ushort)n), rtcp)).ToArray();
            Recover(receiver, packets[0], rtcp ? Rtcp() : Rtp(0), rtcp);
            Recover(receiver, packets[69], rtcp ? Rtcp() : Rtp(69), rtcp);
            Reject(receiver, packets[0], rtcp);
            Reject(receiver, packets[5], rtcp); // Distance 64 is outside the window.
            Recover(receiver, packets[6], rtcp ? Rtcp() : Rtp(6), rtcp); // Distance 63 is valid once.
            Reject(receiver, packets[6], rtcp);
        }
    }

    internal static void ResourceBounds()
    {
        foreach (var profile in Profiles)
        foreach (var rtcp in new[] { false, true })
        {
            using var sender = Create(profile, SrtpDirection.Send);
            using var receiver = Create(profile, SrtpDirection.Receive);
            var original = rtcp ? Rtcp() : Rtp(1);
            var encrypted = Protect(sender, original, rtcp);
            // Unauthenticated unknown SSRCs cannot consume state capacity.
            for (uint source = 0; source < 100; source++)
            {
                var bad = (byte[])encrypted.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(rtcp ? 4 : 8), source);
                Reject(receiver, bad, rtcp);
            }
            Recover(receiver, encrypted, original, rtcp);
            for (uint source = 0; source < SrtpContext.MaximumSources - 1; source++)
            {
                var plain = rtcp ? Rtcp(source) : Rtp(1, source);
                Recover(receiver, Protect(sender, plain, rtcp), plain, rtcp);
            }
            var extra = rtcp ? Rtcp(999) : Rtp(1, 999);
            var output = new byte[128];
            Check(!(rtcp ? sender.TryProtectRtcp(extra, output, out _) : sender.TryProtectRtp(extra, output, out _)));
            using var independent = Create(profile, SrtpDirection.Send);
            Reject(receiver, Protect(independent, extra, rtcp), rtcp);
        }
    }

    internal static void BufferBounds()
    {
        foreach (var profile in Profiles)
        {
            using var sender = Create(profile, SrtpDirection.Send);
            using var receiver = Create(profile, SrtpDirection.Receive);
            var plain = Rtp(1);
            var output = Enumerable.Repeat((byte)0xAB, plain.Length + sender.RtpOverhead - 1).ToArray();
            Check(!sender.TryProtectRtp(plain, output, out var written) && written == 0 && output.All(b => b == 0xAB));
            var encrypted = Protect(sender, plain);
            Check(!receiver.TryUnprotectRtp(encrypted, output.AsSpan(0, plain.Length - 1), out _));
            var alias = new byte[256]; plain.CopyTo(alias, 0);
            Check(!sender.TryProtectRtp(alias.AsSpan(0, plain.Length), alias, out _));
            encrypted.CopyTo(alias, 0);
            Check(!receiver.TryUnprotectRtp(alias.AsSpan(0, encrypted.Length), alias, out _));
            Recover(receiver, encrypted, plain);
            var tooLarge = new byte[SrtpContext.MaximumPacketLength + 1]; Rtp(2).CopyTo(tooLarge, 0);
            Check(!sender.TryProtectRtp(tooLarge, new byte[tooLarge.Length + 16], out _));
            var maximum = tooLarge[..SrtpContext.MaximumPacketLength];
            Recover(receiver, Protect(sender, maximum), maximum);
            Check(!sender.TryProtectRtcp(Convert.FromHexString("80c800ffffffffff"), new byte[128], out _));
        }
    }

    internal static void LifetimeAndDirection()
    {
        foreach (var profile in Profiles)
        {
            using var sender = Create(profile, SrtpDirection.Send);
            using var receiver = Create(profile, SrtpDirection.Receive);
            Throws<InvalidOperationException>(() => sender.TryUnprotectRtp(Rtp(1), new byte[128], out _));
            Throws<InvalidOperationException>(() => receiver.TryProtectRtp(Rtp(1), new byte[128], out _));
            sender.Dispose(); sender.Dispose(); receiver.Dispose();
            try { sender.TryProtectRtp(Rtp(1), new byte[128], out _); Check(false); } catch (ObjectDisposedException) { }
            try { _ = new SrtpContext(profile, SrtpDirection.Send, [], Salt(profile)); Check(false); } catch (ArgumentException) { }
            try { _ = new SrtpContext(profile, SrtpDirection.Send, Key(profile), []); Check(false); } catch (ArgumentException) { }
        }
        try { _ = new SrtpContext((SrtpProfile)99, SrtpDirection.Send, new byte[16], new byte[14]); Check(false); } catch (ArgumentOutOfRangeException) { }
    }

    internal static void MalformedInputs()
    {
        var random = new Random(5227);
        foreach (var profile in Profiles)
        {
            using var receiver = Create(profile, SrtpDirection.Receive);
            for (var i = 0; i < 2000; i++)
            {
                var packet = new byte[random.Next(0, 512)]; random.NextBytes(packet);
                if (packet.Length > 12) packet[0] = 0x80;
                Reject(receiver, packet); Reject(receiver, packet, true);
            }
        }
    }
}
