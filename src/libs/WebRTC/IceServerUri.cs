using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace tryAGI.WebRTC;

/// <summary>Bounded RFC7064/7065 server URI subset. Credentials are supplied separately.</summary>
public sealed class IceServerUri
{
    public string Host { get; }
    public int Port { get; }
    public bool IsTurn { get; }
    public bool IsSecure { get; }
    /// <summary>Server transport; turns/UDP (DTLS) and stuns are parsed but not gatherable yet.</summary>
    public TurnServerTransport Transport { get; }
    public bool SupportsGathering => IsTurn ? !(IsSecure && Transport == TurnServerTransport.Udp) : !IsSecure;
    private IceServerUri(string host, int port, bool turn, bool secure, TurnServerTransport transport)
    { Host = host; Port = port; IsTurn = turn; IsSecure = secure; Transport = transport; }

    public static IceServerUri Parse(string value) => TryParse(value, out var result) ? result! :
        throw new FormatException("Unsupported or malformed ICE server URI.");

    /// <summary>ASCII DNS labels, canonical IPv4 and bracketed IPv6 only; no userinfo, paths, scopes or fragments.</summary>
    public static bool TryParse(string? value, out IceServerUri? result)
    {
        result = null;
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Any(c => c <= ' ' || c >= 127)) return false;
        var colon = value.IndexOf(':'); if (colon < 0) return false;
        var scheme = value[..colon].ToLowerInvariant();
        if (scheme is not ("stun" or "stuns" or "turn" or "turns")) return false;
        var turn = scheme.StartsWith("turn", StringComparison.Ordinal); var secure = scheme.EndsWith('s');
        var authority = value[(colon + 1)..]; var transport = secure ? TurnServerTransport.Tls : TurnServerTransport.Udp;
        var query = authority.IndexOf('?');
        if (query >= 0)
        {
            if (!turn) return false;
            var text = authority[(query + 1)..]; authority = authority[..query];
            if (text.Equals("transport=udp", StringComparison.OrdinalIgnoreCase)) transport = TurnServerTransport.Udp;
            else if (text.Equals("transport=tcp", StringComparison.OrdinalIgnoreCase)) transport = secure ? TurnServerTransport.Tls : TurnServerTransport.Tcp;
            else return false;
        }
        if (authority.Length == 0 || authority.IndexOfAny(['/', '\\', '#', '@', '%', '?']) >= 0) return false;
        string host; string? portText = null;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']'); if (close <= 1) return false;
            host = authority[1..close];
            if (close + 1 < authority.Length)
            { if (authority[close + 1] != ':') return false; portText = authority[(close + 2)..]; }
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6) return false;
            host = address.ToString();
        }
        else
        {
            var portColon = authority.IndexOf(':');
            host = portColon < 0 ? authority : authority[..portColon];
            if (portColon >= 0) portText = authority[(portColon + 1)..];
            if (IPAddress.TryParse(host, out var address))
            { if (address.AddressFamily != AddressFamily.InterNetwork || host != address.ToString()) return false; }
            else if (!DnsHost(host)) return false;
            host = host.ToLowerInvariant();
        }
        var port = secure ? 5349 : 3478;
        if (portText != null && (portText.Length is < 1 or > 5 || portText.Any(c => c is < '0' or > '9') ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) return false;
        result = new(host, port, turn, secure, transport); return true;
    }
    private static bool DnsHost(string host)
    {
        if (host.Length is < 1 or > 253 || host.All(c => c is >= '0' and <= '9' or '.')) return false;
        var labels = (host.EndsWith('.') ? host[..^1] : host).Split('.');
        return labels.All(label => label.Length is >= 1 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
    public override string ToString()
    {
        var host = Host.Contains(':') ? $"[{Host}]" : Host;
        return $"{(IsTurn ? "turn" : "stun")}{(IsSecure ? "s" : "")}:{host}:{Port}" +
            (IsTurn ? $"?transport={(Transport == TurnServerTransport.Udp ? "udp" : "tcp")}" : "");
    }

    /// <summary>One A/AAAA lookup, pinned bounded endpoints, no SRV/NAPTR/mDNS or implicit server contact.</summary>
    public async Task<IReadOnlyList<IPEndPoint>> ResolveAsync(AddressFamily family, IceServerResolutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) throw new ArgumentOutOfRangeException(nameof(family));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ResolveTimeout);
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(Host, out var literal) ? [literal] :
                await Dns.GetHostAddressesAsync(Host, family, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (addresses.Length > options.MaximumAddresses) throw new IOException("ICE server address inventory exceeds its budget.");
            var endpoints = new List<IPEndPoint>();
            foreach (var address in addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (address.AddressFamily != family) continue;
                IPEndPoint endpoint;
                try { endpoint = new IceCandidate(new(address, Port)).EndPoint; }
                catch (ArgumentException) { continue; }
                if (endpoints.Any(e => e.Equals(endpoint))) continue;
                // Predicates receive a copy: mutation cannot redirect the pinned destination.
                if (options.EndpointFilter(new IceCandidate(endpoint).EndPoint)) endpoints.Add(endpoint);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (endpoints.Count == 0) throw new IOException("ICE server has no admitted address in the local interface family.");
            return endpoints.AsReadOnly();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("ICE server name resolution timed out."); }
    }
}

/// <summary>Application-owned destination admission. Predicates must be pure, fast and nonblocking.</summary>
public sealed record IceServerResolutionOptions
{
    public required Func<IPEndPoint, bool> EndpointFilter { get; init; }
    public int MaximumAddresses { get; init; } = 8;
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Total resolution plus sequential gathering deadline, not a per-address deadline.</summary>
    public TimeSpan GatherTimeout { get; init; } = TimeSpan.FromSeconds(45);
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(EndpointFilter);
        if (MaximumAddresses is < 1 or > 16 || ResolveTimeout < TimeSpan.FromMilliseconds(100) || ResolveTimeout > TimeSpan.FromSeconds(15) ||
            GatherTimeout < TimeSpan.FromMilliseconds(100) || GatherTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(IceServerResolutionOptions));
    }
}
