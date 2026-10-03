using System.Security.Cryptography;
using System.Text;

namespace tryAGI.WebRTC;

/// <summary>ASCII long-term TURN credentials. Unicode PRECIS preparation is not supported.</summary>
public sealed class TurnCredentials
{
    public string Username { get; }
    private readonly string _password;
    public TurnCredentials(string username, string password)
    {
        Validate(username, 512); Validate(password, 1024);
        Username = username; _password = password;
    }
    internal static void Validate(string value, int maximum)
    {
        if (value == null || value.Length is < 1 || value.Length > maximum || value.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("TURN strings require bounded printable ASCII; Unicode preparation is unsupported.");
    }
    internal byte[] Derive(string realm, bool sha256)
    {
        Validate(realm, 128);
        var bytes = Encoding.UTF8.GetBytes(Username + ":" + realm + ":" + _password);
        try { return sha256 ? SHA256.HashData(bytes) : MD5.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public override string ToString() => "TURN credentials (redacted)";
}
