using System.Buffers.Binary;
using tryAGI.WebRTC;

// Shared synthetic workload: fixture setup, key changes and semantic validation
// stay outside the benchmark window. No provider or network traffic is involved.
internal sealed class RejectionAllocationChecks : IDisposable
{
    internal static readonly string[] Modes = ["bad-tag", "wrong-key", "replay"];
    private readonly SrtpContext _receiver;
    private readonly byte[] _ciphertext;
    private readonly byte[] _valid;
    private readonly byte[] _plain;
    private readonly byte[] _output;
    private readonly bool _rtcp;
    private readonly bool _replay;

    internal RejectionAllocationChecks(SrtpProfile profile, bool rtcp, int payload, string mode, bool authenticationOnly = false)
    {
        if (!Modes.Contains(mode)) throw new ArgumentException("Unknown rejection mode");
        _rtcp = rtcp; _replay = mode == "replay";
        var key = new byte[profile == SrtpProfile.AeadAes256Gcm ? 32 : 16];
        var salt = new byte[profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12];
        Random.Shared.NextBytes(key); Random.Shared.NextBytes(salt);
        _plain = rtcp ? Convert.FromHexString(payload == 0 ? "80c9000121324354" : "80c80006213243541234567812345678876543210000000400000005") : new byte[12 + payload];
        if (authenticationOnly)
        {
            if (!rtcp || payload == 0 || profile == SrtpProfile.Aes128CmHmacSha1_80) throw new ArgumentException("Invalid GMAC vector case");
            // Independent Pion v3.1.3 fixtures, with the same keys/salts as SrtpTests.
            for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
            for (var i = 0; i < salt.Length; i++) salt[i] = (byte)(i + 61);
        }
        if (!rtcp) { _plain[0] = 0x80; _plain[1] = 111; _plain[11] = 42; BinaryPrimitives.WriteUInt16BigEndian(_plain.AsSpan(2), 1); }
        using var sender = new SrtpContext(profile, SrtpDirection.Send, key, salt);
        _ciphertext = authenticationOnly ? Convert.FromHexString(profile == SrtpProfile.AeadAes128Gcm
            ? "80c80006213243541234567812345678876543210000000400000005a2fe68080089233af594a363ca9b612000000001"
            : "80c800062132435412345678123456788765432100000004000000055bd049c25e726158ec5db98f2cd440f300000001") : Protect(sender);
        if (mode == "wrong-key") key[0] ^= 1;
        _receiver = new SrtpContext(profile, SrtpDirection.Receive, key, salt);
        // With a wrong receive key, valid ciphertext must come from that key's
        // sender, at the SAME source/index. Acceptance proves no replay poisoning.
        // For E=0 vectors the recovery packet uses E=1, sharing that replay state.
        if (mode == "wrong-key")
        {
            using var matchingSender = new SrtpContext(profile, SrtpDirection.Send, key, salt);
            _valid = Protect(matchingSender);
        }
        else _valid = (byte[])_ciphertext.Clone();
        if (mode == "bad-tag") _ciphertext[rtcp && profile != SrtpProfile.Aes128CmHmacSha1_80 ? _ciphertext.Length - 5 : _ciphertext.Length - 1] ^= 1;
        _output = new byte[_plain.Length];
        if (_replay) Accept();
        _output.AsSpan().Fill(0xAB);
    }

    private byte[] Protect(SrtpContext sender)
    {
        var data = new byte[_plain.Length + (_rtcp ? sender.RtcpOverhead : sender.RtpOverhead)];
        var accepted = _rtcp ? sender.TryProtectRtcp(_plain, data, out var length) : sender.TryProtectRtp(_plain, data, out length);
        if (!accepted || length != data.Length) throw new IOException("Rejection fixture encryption failed");
        return data;
    }

    internal void Exercise(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var accepted = _rtcp ? _receiver.TryUnprotectRtcp(_ciphertext, _output, out var length) : _receiver.TryUnprotectRtp(_ciphertext, _output, out length);
            if (accepted || length != 0) throw new IOException("Rejected packet was accepted");
            foreach (var value in _output) if (value != 0xAB) throw new IOException("Rejected packet changed caller output");
        }
    }

    private void Accept()
    {
        var accepted = _rtcp ? _receiver.TryUnprotectRtcp(_valid, _output, out var length) : _receiver.TryUnprotectRtp(_valid, _output, out length);
        if (!accepted || !_plain.AsSpan().SequenceEqual(_output.AsSpan(0, length))) throw new IOException("Failed authentication poisoned valid packet state");
    }

    internal void VerifyAfterMeasurement()
    {
        if (!_replay) Accept();
    }

    public void Dispose() => _receiver.Dispose();

    internal static void GmacContracts()
    {
        foreach (var profile in new[] { SrtpProfile.AeadAes128Gcm, SrtpProfile.AeadAes256Gcm })
        foreach (var packet in new[] { "rtp-empty", "rtcp-empty", "rtcp-auth-only" })
        foreach (var mode in Modes)
        {
            var rtcp = packet != "rtp-empty";
            using var fixture = new RejectionAllocationChecks(profile, rtcp, packet == "rtcp-auth-only" ? 60 : 0, mode, packet == "rtcp-auth-only");
            fixture.Exercise(128);
            var before = GC.GetAllocatedBytesForCurrentThread();
            fixture.Exercise(256);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (bytes != 0) throw new IOException($"GMAC {profile}/{packet}/{mode} allocated {bytes}");
            if (mode == "bad-tag")
            {
                // Exhaust all 128 tag bits without accepting/advancing this index.
                // A valid packet after this corpus must still be admitted.
                fixture._valid.CopyTo(fixture._ciphertext, 0);
                var offset = fixture._ciphertext.Length - (rtcp ? 20 : 16);
                for (var i = 0; i < 16; i++)
                for (var bit = 0; bit < 8; bit++)
                {
                    fixture._ciphertext[offset + i] ^= (byte)(1 << bit);
                    fixture.Exercise(1);
                    fixture._ciphertext[offset + i] ^= (byte)(1 << bit);
                }
            }
            fixture.VerifyAfterMeasurement();
        }
    }

    internal static void Contracts()
    {
        foreach (var profile in Enum.GetValues<SrtpProfile>())
        foreach (var rtcp in new[] { false, true })
        foreach (var mode in Modes)
        {
            using var fixture = new RejectionAllocationChecks(profile, rtcp, 60, mode);
            fixture.Exercise(128);
            var before = GC.GetAllocatedBytesForCurrentThread();
            fixture.Exercise(256);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if ((profile == SrtpProfile.Aes128CmHmacSha1_80 || mode == "replay") && bytes != 0)
                throw new IOException($"Rejection guard {profile}/{rtcp}/{mode} allocated {bytes}");
            fixture.VerifyAfterMeasurement();
        }
    }
}
