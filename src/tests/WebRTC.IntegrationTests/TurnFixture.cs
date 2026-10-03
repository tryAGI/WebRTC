using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using tryAGI.WebRTC;

// Authored synthetic TURN server for hostile/lifecycle/native tests. Independent Pion is the positive interoperability gate.
internal sealed class TurnFixture : IAsyncDisposable
{
    internal const string Username = "synthetic-user", Secret = "synthetic-password", Realm = "synthetic.local";
    internal readonly Socket Control, Relay, Echo;
    private readonly Socket _attacker;
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(15));
    private readonly Task _run, _relay, _echo;
    internal bool Modern, Stale, Hostile, DropFirst;
    internal bool MissingAlgorithms { get; init; }
    internal bool BadMappedFamily { get; init; }
    internal uint Lifetime = 600;
    internal string AuthUsername { get; init; } = Username;
    internal int HoldMethod { get; set; }
    internal int HoldMilliseconds { get; set; }
    internal bool HoldRefresh { get; set; }
    internal int Allocations, Deletes, Refreshes, Permissions, ChannelCalls, Requests;
    private string _nonce = "nonce-original";
    private IPEndPoint? _client, _peer;
    private ushort _channel;
    internal TurnFixture(bool ipv6 = false, bool modern = false)
    {
        Modern = modern; if (Modern) _nonce = "obMatJos2wAAAoriginal";
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        Control = Socket(address); Relay = Socket(address); Echo = Socket(address); _attacker = Socket(address);
        _run = Run(); _relay = RelayLoop(); _echo = EchoLoop();
    }
    internal static Socket Socket(IPAddress ip)
    { var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp); socket.Bind(new IPEndPoint(ip, 0)); return socket; }
    internal IPEndPoint Server => (IPEndPoint)Control.LocalEndPoint!;
    internal IPEndPoint Peer => (IPEndPoint)Echo.LocalEndPoint!;
    internal static TurnUdpOptions Fast(int peers = 32) => new()
    { Transactions = new StunGatheringOptions { InitialRetransmissionTimeout=TimeSpan.FromMilliseconds(100),MaximumRequests=3,FinalWaitMultiplier=2,Timeout=TimeSpan.FromSeconds(3) }, MaximumPeers = peers };
    private byte[] Key => Modern ? SHA256.HashData(Encoding.ASCII.GetBytes(AuthUsername + ":" + Realm + ":" + Secret)) : MD5.HashData(Encoding.ASCII.GetBytes(AuthUsername + ":" + Realm + ":" + Secret));
    internal static void Check(bool value, string message = "Synthetic TURN assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private byte[] Reply(byte[] request, IPEndPoint client, int code = 0, bool wrongKey = false, bool wrongId = false)
    {
        StunMessage.TryParse(request, out var input); var type = input.Type;
        var id = input.TransactionId.ToArray(); if (wrongId) id[0] ^= 1;
        var bytes = new byte[512]; var writer = new StunMessageWriter(bytes, (ushort)(type | (code == 0 ? 0x0100 : 0x0110)), id);
        if (code != 0)
        {
            Check(writer.TryAddAttribute(0x0009, [0,0,(byte)(code / 100),(byte)(code % 100)]));
            Check(writer.TryAddAttribute(0x0014, Encoding.ASCII.GetBytes(Realm)));
            Check(writer.TryAddAttribute(0x0015, Encoding.ASCII.GetBytes(_nonce)));
            if (Modern && !MissingAlgorithms) Check(writer.TryAddAttribute(0x8002, [0,1,0,0,0,2,0,0]));
        }
        else if (type is 3 or 4)
        {
            if (type == 3)
            {
                Check(writer.TryAddXorAddress(0x0016, (IPEndPoint)Relay.LocalEndPoint!));
                Check(writer.TryAddXorMappedAddress(BadMappedFamily ? new IPEndPoint(IPAddress.IPv6Loopback,1234) : client));
            }
            Check(input.TryGetUniqueAttribute(0x000D, out var requested));
            Check(writer.TryAddUInt32(0x000D, BinaryPrimitives.ReadUInt32BigEndian(requested) == 0 ? 0 : Lifetime));
        }
        var key = code == 401 ? [] : wrongKey ? new byte[16] : Key;
        Check(writer.TryComplete(key, true, out var size, Modern && code != 401)); return bytes[..size];
    }
    private async Task Run()
    {
        try
        {
            while (true)
            {
                var bytes = new byte[2048]; var read = await ReceiveAsync(Control, bytes);
                var packet = bytes[..read.ReceivedBytes]; var source = (IPEndPoint)read.RemoteEndPoint;
                if (_client != null && !source.Equals(_client)) continue;
                _client = source;
                if (packet.Length >= 4 && packet[0] is >= 0x40 and <= 0x4F)
                { Check(_peer != null && BinaryPrimitives.ReadUInt16BigEndian(packet) == _channel); await Relay.SendToAsync(packet.AsMemory(4, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2))),SocketFlags.None,_peer!,_lifetime.Token); continue; }
                Check(StunMessage.TryParse(packet, out var message));
                if (message.Type == 0x0016)
                { Check(message.TryGetXorEndpoint(0x0012,out var destination) && _peer != null); Check(message.TryGetUniqueAttribute(0x0013,out var data)); var payload=data.ToArray(); await Relay.SendToAsync(payload, SocketFlags.None,destination!,_lifetime.Token); continue; }
                Requests++;
                if (DropFirst) { DropFirst=false; continue; }
                if (!message.TryGetUniqueAttribute(Modern ? StunMessage.MessageIntegritySha256 : StunMessage.MessageIntegrity, out _))
                { await Control.SendToAsync(Reply(packet,source,401),SocketFlags.None,source,_lifetime.Token); continue; }
                Check(Modern ? message.VerifyMessageIntegritySha256(Key) : message.VerifyMessageIntegritySha1(Key), "TURN request did not match independent fixture key");
                Check(message.TryGetUniqueAttribute(0x0014,out var realm) && realm.SequenceEqual(Encoding.ASCII.GetBytes(Realm)));
                if (Modern)
                {
                    Check(message.TryGetUniqueAttribute(0x001E,out var userhash) && userhash.SequenceEqual(SHA256.HashData(Encoding.ASCII.GetBytes(AuthUsername+":"+Realm))));
                    Check(message.TryGetUniqueAttribute(0x001D,out var algorithm) && algorithm.SequenceEqual(new byte[]{0,2,0,0}));
                    Check(message.TryGetUniqueAttribute(0x8002,out var algorithms) && algorithms.SequenceEqual(new byte[]{0,1,0,0,0,2,0,0}));
                }
                if (Stale) { Stale=false; _nonce=Modern ? "obMatJos2wAAArenewed" : "nonce-renewed"; await Control.SendToAsync(Reply(packet,source,438),SocketFlags.None,source,_lifetime.Token); continue; }
                var method=message.Type;
                if (method == HoldMethod) { HoldMethod=0; await Task.Delay(HoldMilliseconds,_lifetime.Token); }
                if (method==4 && HoldRefresh) continue;
                Check(StunMessage.TryParse(packet, out var controlMessage));
                switch (method)
                {
                    case 3: Allocations++; break;
                    case 4:
                        Check(controlMessage.TryGetUniqueAttribute(0x000D,out var requested));
                        if (BinaryPrimitives.ReadUInt32BigEndian(requested)==0) { Deletes++; Allocations=0; } else Refreshes++;
                        break;
                    case 8: Check(controlMessage.TryGetXorEndpoint(0x0012,out _peer)); Permissions++; break;
                    case 9: Check(controlMessage.TryGetXorEndpoint(0x0012,out _peer)); Check(controlMessage.TryGetUniqueAttribute(0x000C,out var value)); _channel=BinaryPrimitives.ReadUInt16BigEndian(value); ChannelCalls++; break;
                    default: throw new InvalidDataException("Unexpected fixture method");
                }
                if (Hostile)
                {
                    Hostile=false;
                    await _attacker.SendToAsync(Reply(packet,source),SocketFlags.None,source,_lifetime.Token);
                    await Control.SendToAsync(Reply(packet,source,wrongId:true),SocketFlags.None,source,_lifetime.Token);
                    await Control.SendToAsync(Reply(packet,source,wrongKey:true),SocketFlags.None,source,_lifetime.Token);
                }
                await Control.SendToAsync(Reply(packet,source),SocketFlags.None,source,_lifetime.Token);
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }
    private async ValueTask<SocketReceiveFromResult> ReceiveAsync(Socket socket, Memory<byte> buffer)
    {
        while (true)
        {
            try
            {
                return await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(Server.Address, 0), _lifetime.Token);
            }
            catch (SocketException error) when (!_lifetime.IsCancellationRequested &&
                error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
            {
                // Windows can surface a previous reply's ICMP port-unreachable on
                // the next UDP receive after its peer closed. It is not a failure
                // of this multi-peer synthetic server or its next transaction.
            }
        }
    }
    private async Task EchoLoop()
    {
        try { while (true) { var bytes=new byte[2048]; var read=await ReceiveAsync(Echo, bytes); await Echo.SendToAsync(bytes.AsMemory(0,read.ReceivedBytes),SocketFlags.None,read.RemoteEndPoint,_lifetime.Token); } }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task RelayLoop()
    {
        try
        {
            while (true)
            {
                var bytes=new byte[2048]; var read=await ReceiveAsync(Relay, bytes);
                var source=(IPEndPoint)read.RemoteEndPoint; if (_peer==null || !source.Address.Equals(_peer.Address) || _client==null) continue;
                var reply=new byte[64+read.ReceivedBytes];
                if (_channel!=0)
                { BinaryPrimitives.WriteUInt16BigEndian(reply,_channel); BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2),(ushort)read.ReceivedBytes); bytes.AsSpan(0,read.ReceivedBytes).CopyTo(reply.AsSpan(4)); reply=reply[..(read.ReceivedBytes+4)]; }
                else
                { var writer=new StunMessageWriter(reply,0x0017,RandomNumberGenerator.GetBytes(12)); Check(writer.TryAddXorAddress(0x0012,source)); Check(writer.TryAddAttribute(0x0013,bytes.AsSpan(0,read.ReceivedBytes))); Check(writer.TryComplete([],true,out var size)); reply=reply[..size]; }
                await Control.SendToAsync(reply,SocketFlags.None,_client,_lifetime.Token);
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }
    internal static async Task RoundTrip(bool ipv6=false, bool modern=false, bool channel=false, bool colonUsername=false)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct=deadline.Token;
        await using var fixture=new TurnFixture(ipv6,modern) { Stale=true, Hostile=true, DropFirst=true, AuthUsername=colonUsername?"1791060000:synthetic-user":Username };
        await using var client=await TurnUdpAllocation.AllocateAsync(new(fixture.Server.Address,0),fixture.Server,new(fixture.AuthUsername,Secret),Fast(),ct);
        Check(client.Candidate.Type==IceCandidateType.Relay && client.MappedEndPoint.Equals(client.LocalEndPoint));
        Check(client.Candidate.RelatedEndPoint!.Equals(client.MappedEndPoint));
        if(channel) await client.BindChannelAsync(fixture.Peer,ct); else await client.CreatePermissionAsync(fixture.Peer,ct);
        await client.SendDatagramAsync(fixture.Peer,"owned-turn"u8.ToArray(),ct);
        await foreach(var packet in client.ReceiveDatagramsAsync(ct)) { Check(packet.Source.Equals(fixture.Peer) && packet.Data.AsSpan().SequenceEqual("owned-turn"u8)); break; }
        await client.RefreshAsync(ct);
        Check(client.GetDiagnostics() is {AllocationActive:true, Retransmissions:>=1, RejectedPackets:>=3, ReceivedDatagrams:1} && client.GetDiagnostics().ModernIntegrity==modern);
        await client.DisposeAsync(); Check(client.GetDiagnostics().GracefulReleaseAcknowledged && fixture.Deletes==1 && fixture.Allocations==0);
    }
    public async ValueTask DisposeAsync()
    { _lifetime.Cancel(); Control.Dispose(); Relay.Dispose(); Echo.Dispose(); _attacker.Dispose(); await Task.WhenAll(_run,_relay,_echo); _lifetime.Dispose(); }
}
