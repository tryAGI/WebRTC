using System.Net;
using System.Net.Sockets;

namespace tryAGI.WebRTC;

public enum IceRole { Controlling, Controlled }
public enum IceCandidateType { Host, ServerReflexive, PeerReflexive, Relay }

/// <summary>A resolved RTP-component UDP candidate. Resolver and relay routing are separate concerns.</summary>
public sealed class IceCandidate
{
    public IPEndPoint EndPoint => new(CloneAddress(_address), _port);
    internal IPEndPoint TransportEndPoint { get; }
    public uint Priority { get; }
    public IceCandidateType Type { get; }
    private readonly IPAddress _address;
    private readonly int _port;

    public IceCandidate(IPEndPoint endPoint, uint priority = 2130706431, IceCandidateType type = IceCandidateType.Host)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        var address = endPoint.Address;
        if (endPoint.Port == 0 || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) ||
            address.IsIPv6Multicast || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] >= 224) ||
            address.IsIPv4MappedToIPv6 || priority == 0 || priority > int.MaxValue || !Enum.IsDefined(type))
            throw new ArgumentException("An ICE candidate needs a unicast IP address, nonzero port and priority.", nameof(endPoint));
        // Copy mutable address/scope state at the API boundary.
        _address = CloneAddress(address);
        _port = endPoint.Port;
        TransportEndPoint = new(_address, _port);
        Priority = priority;
        Type = type;
    }

    private static IPAddress CloneAddress(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6
        ? new IPAddress(address.GetAddressBytes(), address.ScopeId) : new IPAddress(address.GetAddressBytes());
}
