using tryAGI.WebRTC;

internal static class IdentityTests
{
    internal static void FreshAndOwned()
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 16; i++)
        {
            using var identity = DtlsIdentity.Generate();
            var fingerprint = identity.GetFingerprintSha256();
            if (fingerprint.Length != 32 || !fingerprints.Add(Convert.ToHexString(fingerprint)))
                throw new InvalidOperationException("Each identity must have a fresh SHA256 fingerprint.");
            var expected = identity.FingerprintSha256;
            fingerprint.AsSpan().Clear();
            if (identity.FingerprintSha256 != expected || identity.GetFingerprintSha256().All(b => b == 0))
                throw new InvalidOperationException("Fingerprint callers must not mutate identity state.");
            identity.Dispose();
            identity.Dispose();
            if (identity.FingerprintSha256 != expected)
                throw new InvalidOperationException("Fingerprint evidence must survive idempotent disposal.");
        }
    }
}
