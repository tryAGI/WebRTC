using System.Security.Cryptography;

namespace tryAGI.WebRTC;

/// <summary>One ICE generation's short-term credentials. Do not log or reuse the password.</summary>
public sealed class IceCredentials
{
    public string UsernameFragment { get; }
    public string Password { get; }

    public IceCredentials(string usernameFragment, string password)
    {
        Validate(usernameFragment, 4, nameof(usernameFragment));
        Validate(password, 22, nameof(password));
        UsernameFragment = usernameFragment;
        Password = password;
    }

    public static IceCredentials Generate() => new(
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(33)));

    public override string ToString() => $"ICE {UsernameFragment} (password redacted)";

    private static void Validate(string value, int minimumLength, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length < minimumLength || value.Length > 256 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/')))
            throw new ArgumentException("ICE credentials must contain only ICE characters and meet RFC 8445 length bounds.", name);
    }
}
