using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace tryAGI.WebRTC;

/// <summary>An ephemeral P-256 certificate identity. No private key is exported or persisted.</summary>
public sealed class DtlsIdentity : IDisposable
{
    private readonly object _gate = new();
    private readonly ECDsa _key;
    private readonly byte[] _certificate;
    private readonly byte[] _fingerprint;
    private bool _disposed;

    private DtlsIdentity(ECDsa key, byte[] certificate)
    {
        _key = key;
        _certificate = certificate;
        _fingerprint = SHA256.HashData(certificate);
    }

    public byte[] GetFingerprintSha256() => (byte[])_fingerprint.Clone();
    public string FingerprintSha256 => string.Join(':', _fingerprint.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    public static DtlsIdentity Generate()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var request = new CertificateRequest("CN=tryAGI.WebRTC", key, HashAlgorithmName.SHA256);
            var now = DateTimeOffset.UtcNow;
            // The identity already owns the signing key. A private-key-associated X509
            // object adds an unnecessary native key import (especially on macOS).
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            serial[0] |= 1;
            using var certificate = request.Create(request.SubjectName,
                X509SignatureGenerator.CreateForECDsa(key), now.AddMinutes(-5), now.AddDays(30), serial);
            return new DtlsIdentity(key, certificate.RawData);
        }
        catch { key.Dispose(); throw; }
    }

    internal byte[] Certificate => (byte[])_certificate.Clone();
    internal byte[] Sign(ReadOnlySpan<byte> hash)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _key.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _key.Dispose();
        }
    }
}
