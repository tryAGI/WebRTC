using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using tryAGI.WebRTC;

internal static class TurnTests
{
    private static void Check(bool value, string message="TURN assertion failed") => TurnFixture.Check(value,message);
    internal static async Task Lifetime()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct=deadline.Token;
        await using var fixture=new TurnFixture {Lifetime=2};
        await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast(),ct);
        while(fixture.Refreshes<2) await Task.Delay(20,ct);
        Check(allocation.GetDiagnostics().AllocationActive && !allocation.Completion.IsCompleted,"Allocation was not maintained");
    }
    internal static async Task PolicyAndBounds()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct=deadline.Token;
        await using var fixture=new TurnFixture();
        await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast(1) with {PeerFilter=p=>p.Port!=1},ct);
        try { await allocation.CreatePermissionAsync(new(IPAddress.Loopback,1),ct); throw new Exception("Forbidden peer accepted"); } catch(ArgumentException) { }
        await allocation.CreatePermissionAsync(fixture.Peer,ct);
        await allocation.BindChannelAsync(fixture.Peer,ct);
        try { await allocation.CreatePermissionAsync(new(IPAddress.Parse("127.0.0.2"),fixture.Peer.Port),ct); throw new Exception("Permission budget exceeded"); } catch(InvalidOperationException) { }
        try { await allocation.BindChannelAsync(new(IPAddress.Loopback,fixture.Peer.Port==65535?2:fixture.Peer.Port+1),ct); throw new Exception("Channel budget exceeded"); } catch(InvalidOperationException) { }
        var bytes=new byte[128]; var writer=new StunMessageWriter(bytes,0x0017,new byte[12]);
        Check(writer.TryAddXorAddress(0x0012,new(IPAddress.Loopback,1))); Check(writer.TryAddAttribute(0x0013,"forbidden"u8)); Check(writer.TryComplete([],true,out var length));
        await fixture.Control.SendToAsync(bytes.AsMemory(0,length),System.Net.Sockets.SocketFlags.None,allocation.LocalEndPoint,ct);
        while(allocation.GetDiagnostics().RejectedPackets==0) await Task.Delay(5,ct);
        try { await allocation.SendDatagramAsync(fixture.Peer,new byte[1201],ct); throw new Exception("Oversized data accepted"); } catch(ArgumentOutOfRangeException) { }
        Check(allocation.GetDiagnostics() is {Permissions:1,Channels:1});
        await allocation.SendDatagramAsync(fixture.Peer,ReadOnlyMemory<byte>.Empty,ct);
        await foreach(var packet in allocation.ReceiveDatagramsAsync(ct)) { Check(packet.Data.Length==0); break; }
    }
    internal static async Task DowngradeOrAddress(bool address)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct=deadline.Token;
        await using var fixture=new TurnFixture(modern:!address) {MissingAlgorithms=!address,BadMappedFamily=address};
        try { await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast(),ct); throw new Exception("Unsafe allocation accepted"); }
        catch(InvalidDataException) { }
        if(address) Check(fixture.Deletes==1,"Unsupported family leaked acknowledged allocation");
        else Check(fixture.Requests==1,"Downgrade retried credentials");
    }
    internal static async Task LegacyRefusal()
    {
        await using var fixture=new TurnFixture();
        try { await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast() with {AllowLegacyAuthentication=false}); throw new Exception("Legacy credentials used against policy"); }
        catch(NotSupportedException) { }
        Check(fixture.Requests==1);
    }
    internal static async Task Cancellation(bool dispose, bool blocked=false)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct=deadline.Token;
        await using var fixture=new TurnFixture {HoldMethod=8,HoldMilliseconds=blocked?4000:250};
        await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast() with {Transactions=blocked? new StunGatheringOptions { InitialRetransmissionTimeout=TimeSpan.FromMilliseconds(100),MaximumRequests=7,Timeout=TimeSpan.FromSeconds(5)}:TurnFixture.Fast().Transactions},ct);
        using var cancel=new CancellationTokenSource();
        var pending=allocation.CreatePermissionAsync(fixture.Peer,cancel.Token);
        try { await allocation.RefreshAsync(ct); throw new Exception("Concurrent control operation admitted"); } catch(InvalidOperationException) { }
        await Task.Delay(30,ct);
        if(dispose) { await allocation.DisposeAsync().AsTask().WaitAsync(ct); try { await pending.WaitAsync(ct); } catch(OperationCanceledException) { } catch(ObjectDisposedException) { } Check(allocation.Completion.IsCompleted); if(blocked) Check(!allocation.GetDiagnostics().GracefulReleaseAcknowledged,"Forced close falsely claimed remote deletion"); return; }
        cancel.Cancel();
        try { await pending; throw new Exception("Canceled control completed"); } catch(OperationCanceledException) { }
        await Task.Delay(300,ct); Check(!allocation.Completion.IsCompleted);
        await allocation.CreatePermissionAsync(fixture.Peer,ct);
        await allocation.SendDatagramAsync(fixture.Peer,"after-cancel"u8.ToArray(),ct);
        await foreach(var packet in allocation.ReceiveDatagramsAsync(ct)) { Check(packet.Data.AsSpan().SequenceEqual("after-cancel"u8)); break; }
    }
    internal static async Task ReleaseTimeout()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct=deadline.Token;
        await using var fixture=new TurnFixture();
        await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast(),ct);
        fixture.HoldRefresh=true;
        await allocation.DisposeAsync().AsTask().WaitAsync(ct);
        Check(allocation.Completion.IsCompleted && !allocation.GetDiagnostics().AllocationActive && !allocation.GetDiagnostics().GracefulReleaseAcknowledged);
    }
    internal static async Task Expiry()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct=deadline.Token;
        await using var fixture=new TurnFixture {Lifetime=2,HoldRefresh=true};
        await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast(),ct);
        Check(await allocation.Completion.WaitAsync(ct) is TimeoutException);
        Check(!allocation.GetDiagnostics().AllocationActive);
    }
    internal static async Task Oversized()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct=deadline.Token;
        await using var fixture=new TurnFixture();
        await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),fixture.Server,new(TurnFixture.Username,TurnFixture.Secret),TurnFixture.Fast(),ct);
        using var attacker=TurnFixture.Socket(IPAddress.Loopback);
        await attacker.SendToAsync(new byte[9000],System.Net.Sockets.SocketFlags.None,allocation.LocalEndPoint,ct);
        while(allocation.GetDiagnostics().RejectedPackets==0) await Task.Delay(5,ct);
        Check(!allocation.Completion.IsCompleted,"Oversized unknown-source UDP stopped allocation");
        await allocation.RefreshAsync(ct);
    }
    internal static async Task Pion(Uri uri,bool channel)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct=deadline.Token;
        using var http=new HttpClient {BaseAddress=uri};
        using var response=await http.PostAsync("turn",null,ct); response.EnsureSuccessStatusCode();
        var session=(await response.Content.ReadFromJsonAsync(TurnJson.Default.TurnSession,ct))!;
        try
        {
            await using var allocation=await TurnUdpAllocation.AllocateAsync(new(IPAddress.Loopback,0),new(IPAddress.Loopback,session.Port),new(session.Username,session.Password),TurnFixture.Fast(),ct);
            Check((await http.GetFromJsonAsync("turn/"+session.Id,TurnJson.Default.TurnStats,ct))!.Allocations==1);
            var peer=new IPEndPoint(IPAddress.Loopback,session.PeerPort);
            if(channel) await allocation.BindChannelAsync(peer,ct); else await allocation.CreatePermissionAsync(peer,ct);
            var payload=SdpTests.OpusPayload(); await allocation.SendDatagramAsync(peer,payload,ct);
            await foreach(var packet in allocation.ReceiveDatagramsAsync(ct)) { Check(packet.Source.Equals(peer) && packet.Data.AsSpan().SequenceEqual(payload)); break; }
            await allocation.RefreshAsync(ct); await allocation.DisposeAsync();
            Check(allocation.GetDiagnostics().GracefulReleaseAcknowledged && (await http.GetFromJsonAsync("turn/"+session.Id,TurnJson.Default.TurnStats,ct))!.Allocations==0,"Independent server allocation remained");
        }
        finally { using var cleanup=await http.DeleteAsync("turn/"+session.Id,CancellationToken.None); cleanup.EnsureSuccessStatusCode(); }
    }
}
internal sealed record TurnSession(string Id,int Port,int PeerPort,string Username,string Password)
{ public override string ToString()=>"Local TURN fixture (credentials redacted)"; }
internal sealed record TurnStats(int Allocations);
[JsonSerializable(typeof(TurnSession))]
[JsonSerializable(typeof(TurnStats))]
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]
internal partial class TurnJson:JsonSerializerContext;
