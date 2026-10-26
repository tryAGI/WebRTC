using System.Net;
using System.Net.Sockets;

namespace tryAGI.WebRTC;

// One receive loop owns this mutable OS address. Only freshly materialized,
// independent endpoints escape; a later receive never mutates a retained source.
internal sealed class UdpReceiveSource
{
    private readonly IPEndPoint _template;
    private readonly byte[] _previous;
    private int _previousSize;
    private IPEndPoint? _endpoint;
    internal SocketAddress Address { get; }

    internal UdpReceiveSource(AddressFamily family)
    {
        _template = family switch
        {
            AddressFamily.InterNetwork => new(IPAddress.Any, 0),
            AddressFamily.InterNetworkV6 => new(IPAddress.IPv6Any, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        Address = new(family, SocketAddress.GetMaximumAddressSize(family));
        _previous = new byte[Address.Size];
    }

    internal IPEndPoint Resolve()
    {
        var bytes = Address.Buffer.Span[..Address.Size];
        var cached = _endpoint;
        if (cached is not null && _previousSize == bytes.Length && bytes.SequenceEqual(_previous.AsSpan(0, _previousSize)))
            return cached;
        var source = _template.Create(Address) as IPEndPoint ?? throw new IOException("UDP receive source is not an IP endpoint");
        bytes.CopyTo(_previous); _previousSize = bytes.Length; _endpoint = source;
        return source;
    }
}
