using System.Net;
using tryAGI.WebRTC;

internal static class IceServerUriTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("ICE server URI assertion failed"); }
    internal static void Vectors()
    {
        foreach (var (text, host, port, turn, secure, transport, supported) in new[]
        {
            ("stun:example.org", "example.org", 3478, false, false, TurnServerTransport.Udp, true),
            ("stuns:example.org", "example.org", 5349, false, true, TurnServerTransport.Tls, false),
            ("turn:example.org:9999?transport=tcp", "example.org", 9999, true, false, TurnServerTransport.Tcp, true),
            ("turns:example.org?transport=tcp", "example.org", 5349, true, true, TurnServerTransport.Tls, true),
            ("turns:example.org", "example.org", 5349, true, true, TurnServerTransport.Tls, true),
            ("turns:example.org?transport=udp", "example.org", 5349, true, true, TurnServerTransport.Udp, false),
            ("TURN:EXAMPLE.ORG.?TRANSPORT=UDP", "example.org.", 3478, true, false, TurnServerTransport.Udp, true),
            ("stun:192.0.2.1:1", "192.0.2.1", 1, false, false, TurnServerTransport.Udp, true),
            ("turn:[2001:db8::1]:65535?transport=udp", "2001:db8::1", 65535, true, false, TurnServerTransport.Udp, true),
            ("turn:xn--bcher-kva.example", "xn--bcher-kva.example", 3478, true, false, TurnServerTransport.Udp, true),
        })
        {
            var uri = IceServerUri.Parse(text);
            Check(uri.Host == host && uri.Port == port && uri.IsTurn == turn && uri.IsSecure == secure && uri.Transport == transport && uri.SupportsGathering == supported);
            var canonical = IceServerUri.Parse(uri.ToString());
            Check(canonical.ToString() == uri.ToString() && canonical.Transport == transport && canonical.SupportsGathering == supported);
        }
    }
    internal static void Malformed()
    {
        string?[] cases = [null, "", "stun:", "stun://example.org", "https://example.org", "stun: example.org", "stun:example.org\r\n",
            "stun:alice:secret@example.org", "turn:example.org/path", "stun:example.org#frag", "turn:example.org?transport=tcp&foo=x",
            "turn:example.org?transport=udp?transport=tcp", "turn:example.org?transport=sctp", "stun:example.org?transport=udp",
            "stun:example.org:", "stun:example.org:0", "stun:example.org:65536", "stun:example.org:+80", "stun:example.org:1:2",
            "stun:127.1", "stun:0177.0.0.1", "stun:0x7f000001", "stun:2130706433", "stun:256.1.1.1", "stun:12",
            "stun:2001:db8::1", "stun:[::1", "stun:[127.0.0.1]", "stun:[::1]x", "stun:[::1]:", "stun:[::ffff:127.0.0.1]",
            "stun:[fe80::1%25en0]", "stun:[fe80::1%1]", "stun:ex%61mple.org", "stun:bücher.example", "stun:under_score.example",
            "stun:-a.example", "stun:a-.example", "stun:a..example", "stun:.", "stun:a\\b", "stun:a]", "stun:[::1]]",
            "stun:" + new string('a', 64) + ".example", "stun:" + new string('a', 508)];
        foreach (var text in cases)
        {
            Check(!IceServerUri.TryParse(text, out var value) && value == null);
            try { IceServerUri.Parse(text!); throw new InvalidOperationException("Malformed URI accepted"); } catch (FormatException) { }
        }
    }
    internal static void Corpus()
    {
        var random = new Random(7065);
        for (var n = 0; n < 4000; n++)
        {
            var text = new string(Enumerable.Range(0, random.Next(0, 520)).Select(_ => (char)random.Next(0, 256)).ToArray());
            if (IceServerUri.TryParse(text, out var uri)) Check(IceServerUri.Parse(uri!.ToString()).ToString() == uri.ToString());
        }
        var host = string.Join('.', Enumerable.Repeat(new string('a', 63), 4));
        Check(!IceServerUri.TryParse("stun:" + host, out _));
        Check(IceServerUri.TryParse("stun:" + host[..253], out _));
    }
}
