using System.Net;
using System.Net.Sockets;
using System.Text;
using tryAGI.WebRTC;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = deadline.Token;
var address = IPAddress.Parse(args[0]);
var codec=args[2];
if(codec is not ("vp8" or "h264"))throw new ArgumentException("Unknown fixture codec");
using var http = new HttpListener(); http.Prefixes.Add("http://127.0.0.1:9430/"); http.Start();
await using var peer = new PeerConnection(new() {
    LocalEndPoint = new(address, 0), AudioDirection = SdpDirection.SendOnly,
    VideoDirection = SdpDirection.SendOnly,
    VideoCodecs = [codec=="vp8" ? new() { Codec=VideoCodec.Vp8, PayloadType=96, Vp8MaximumMacroblocks=1200, Vp8MaximumFrameRate=30 } :
        new() { Codec=VideoCodec.H264, PayloadType=102, H264ProfileLevelId="42e01f", H264PacketizationMode=1, H264LevelAsymmetryAllowed=true }],
    CandidateFilter = c => c.EndPoint.Address.Equals(address) || IPAddress.IsLoopback(c.EndPoint.Address),
    ConnectionTimeout = TimeSpan.FromSeconds(20), Rtcp = new() { PointToPoint = true }
});
await using var proxy = new DropProxy(address, ct);
var mode = args[1];
if(mode is not ("answer-active" or "answer-passive" or "offer")) throw new ArgumentException("Unknown test mode");
string Advertise(string sdp) {
    proxy.Owned = peer.GetLocalCandidates().Single().EndPoint;
    return string.Join("\r\n", sdp.Split("\r\n").Select(line => {
        if(!line.StartsWith("a=candidate:", StringComparison.Ordinal)) return line;
        var fields=line.Split(' '); fields[5]=proxy.EndPoint.Port.ToString(); return string.Join(' ',fields);
    }));
}
// This test owns at most nine channel generations and observes every receive worker.
var channelWorkers=new List<Task>(); var channelGate=new object(); int acceptedChannels=0, closedChannels=0, channelMessages=0;
void TrackChannel(DataChannel channel) {
    lock(channelGate) {
        if(channelWorkers.Count>=9)throw new IOException("Unexpected channel generation");
        Interlocked.Increment(ref acceptedChannels);channelWorkers.Add(EchoChannel(channel));
    }
}
async Task EchoChannel(DataChannel channel) {
    await foreach(var message in channel.ReceiveMessagesAsync(ct)) {
        Interlocked.Increment(ref channelMessages);
        if(message.Kind==DataChannelMessageKind.Binary)await channel.SendBinaryAsync(message.Data,ct);
        else {
            var text=message.GetText();await channel.SendTextAsync(text=="browser-ready"?"owned-ready":text,ct);
            if(text=="close-owned"){await channel.CloseAsync(ct);break;}
        }
    }
    if(await channel.Completion.WaitAsync(ct) is { } error)throw error;
    Interlocked.Increment(ref closedChannels);
}
void CheckChannelWorkers() { lock(channelGate)foreach(var worker in channelWorkers)if(worker.IsFaulted)worker.GetAwaiter().GetResult(); }
void ValidateIncomingChannel(DataChannel channel) {
    var p=channel.Parameters;
    if(p.Label=="owned" && p.Protocol=="" && p.Ordered && p.Reliability==DataChannelReliability.Reliable)return;
    if(p.Protocol!="tryagi-local")throw new IOException("Unexpected channel protocol");
    var (ordered, reliability, parameter)=p.Label switch {
        "browser-reliable-ordered" or "reopened" => (true,DataChannelReliability.Reliable,0u),
        "browser-reliable-unordered" => (false,DataChannelReliability.Reliable,0u),
        "browser-retransmit-ordered" => (true,DataChannelReliability.RetransmissionLimited,2u),
        "browser-retransmit-unordered" => (false,DataChannelReliability.RetransmissionLimited,2u),
        "browser-timed-ordered" => (true,DataChannelReliability.Timed,1000u),
        "browser-timed-unordered" => (false,DataChannelReliability.Timed,1000u),
        _ => throw new IOException("Unexpected channel label")
    };
    if(p.Ordered!=ordered || p.Reliability!=reliability || p.ReliabilityParameter!=parameter)throw new IOException("DCEP policy was not preserved");
}
Task? connect = null; Task? channels = null; Task? feedback = null; int requests=0, baseline=0;
Console.WriteLine("Owned browser video probe listening");
while (!ct.IsCancellationRequested) {
    var context = await http.GetContextAsync().WaitAsync(ct);
    context.Response.Headers.Add("Access-Control-Allow-Origin", "http://127.0.0.1:9235");
    var path = context.Request.Url!.AbsolutePath; string result;
    try {
        if (path == "/offer" && mode == "offer") {
            result = Advertise(peer.CreateOffer());
        } else if (path == "/answer") {
            if(context.Request.ContentLength64 is < 1 or > 65536) throw new IOException("SDP fixture bound");
            using var reader = new StreamReader(context.Request.InputStream);
            var remote = await reader.ReadToEndAsync(ct);
            if(mode == "offer") { peer.SetRemoteAnswer(remote); result="accepted"; }
            else result = Advertise(peer.CreateAnswer(remote, mode == "answer-passive" ? SdpSetup.Passive : SdpSetup.Active));
            proxy.PayloadType = peer.VideoFormat!.PayloadType;
            connect = peer.ConnectAsync(ct);
            channels = Task.Run(async () => { await foreach (var channel in peer.AcceptDataChannelsAsync(ct)) {
                ValidateIncomingChannel(channel);TrackChannel(channel);
            } }, ct);
            feedback = Task.Run(async () => { await foreach(var request in peer.ReceiveVideoKeyFrameRequestsAsync(ct)) {
                if (request.MediaSource != peer.VideoSource) throw new IOException("Wrong feedback source");
                Interlocked.Increment(ref requests);
            } }, ct);
        } else if(path=="/open-channel") {
            using var opening=CancellationTokenSource.CreateLinkedTokenSource(ct);opening.CancelAfter(TimeSpan.FromSeconds(5));
            TrackChannel(await peer.OpenDataChannelAsync(new("owned-unordered","tryagi-local",false,DataChannelReliability.Timed,1000,256),opening.Token));
            result="opened";
        } else if(path=="/channels") {
            CheckChannelWorkers();if(channels is { IsFaulted:true })await channels;
            result=$"{Volatile.Read(ref acceptedChannels)},{Volatile.Read(ref closedChannels)},{Volatile.Read(ref channelMessages)}";
        } else if (path == "/state") {
            CheckChannelWorkers();
            if (connect is { IsFaulted:true }) await connect;
            if (channels is { IsFaulted:true }) await channels;
            if (feedback is { IsFaulted:true }) await feedback;
            result = peer.State.ToString();
        } else if (path == "/loss") {
            baseline = Volatile.Read(ref requests); proxy.ArmLoss(); result="armed";
        } else if (path == "/feedback") {
            result = $"{Volatile.Read(ref requests)-baseline},{proxy.Dropped},{peer.GetRtcpDiagnostics()?.ReceivedPictureLoss}";
        } else if (path == "/frame") {
            if(context.Request.ContentLength64 is < 1 or > 100000) throw new IOException("Frame fixture bound");
            using var buffer = new MemoryStream(); await context.Request.InputStream.CopyToAsync(buffer, ct);
            var data=buffer.ToArray();
            var time=uint.Parse(context.Request.QueryString["timestamp"]!);
            if(codec=="vp8") {
            // Deliberately fragment even tiny authored frames, exercising real browser RTP reassembly.
            for(var offset=0;offset<data.Length;offset+=24) {
                var length=Math.Min(24,data.Length-offset);var rtp=new byte[length+1];
                rtp[0]=offset==0 ? (byte)0x10 : (byte)0;data.AsSpan(offset,length).CopyTo(rtp.AsSpan(1));
                await peer.SendVideoRtpAsync(rtp,time,offset+length==data.Length,ct);
            }
            } else {
            var starts=new List<(int Offset,int Prefix)>();
            for(var i=0;i<data.Length-2;i++) {
                var prefix=i+3<data.Length && data[i]==0 && data[i+1]==0 && data[i+2]==0 && data[i+3]==1 ? 4 :
                    data[i]==0 && data[i+1]==0 && data[i+2]==1 ? 3 : 0;
                if(prefix==0)continue; starts.Add((i,prefix));i+=prefix-1;
            }
            if(starts.Count==0 || starts[0].Offset!=0)throw new IOException("Expected authored AnnexB fixture");
            for(var n=0;n<starts.Count;n++) {
                var begin=starts[n].Offset+starts[n].Prefix;var end=n+1<starts.Count?starts[n+1].Offset:data.Length;
                var nal=data[begin..end];if(nal.Length==0)throw new IOException("Empty NAL fixture");
                if(nal.Length<=24){await peer.SendVideoRtpAsync(nal,time,n==starts.Count-1,ct);continue;}
                for(var offset=1;offset<nal.Length;offset+=24) {
                    var length=Math.Min(24,nal.Length-offset);var rtp=new byte[length+2];
                    rtp[0]=(byte)((nal[0]&0xe0)|28);rtp[1]=(byte)((nal[0]&0x1f)|(offset==1?0x80:0)|(offset+length==nal.Length?0x40:0));
                    nal.AsSpan(offset,length).CopyTo(rtp.AsSpan(2));
                    await peer.SendVideoRtpAsync(rtp,time,n==starts.Count-1 && offset+length==nal.Length,ct);
                }
            }
            }
            result="sent";
        } else if (path == "/close") { result = "closed"; }
        else { context.Response.StatusCode = 404; result = "unknown"; }
    } catch (Exception e) { Console.Error.WriteLine(e);Console.Error.WriteLine(peer.GetDiagnostics()); context.Response.StatusCode = 500; result = e.GetType().Name; }
    var bytes = Encoding.UTF8.GetBytes(result); context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes, ct); context.Response.Close();
    if (path == "/close") break;
}

sealed class DropProxy : IAsyncDisposable {
    readonly Socket socket=new(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
    readonly CancellationTokenSource lifetime; readonly Task worker;
    public IPEndPoint? Owned {get;set;} public byte PayloadType {get;set;}
    int dropNext, dropped;
    public int Dropped => Volatile.Read(ref dropped);
    public void ArmLoss() => Interlocked.Exchange(ref dropNext, 1);
    public IPEndPoint EndPoint => (IPEndPoint)socket.LocalEndPoint!;
    public DropProxy(IPAddress address,CancellationToken ct) {
        lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);socket.Bind(new IPEndPoint(address,0));
        worker=Task.Run(async()=> { IPEndPoint? browser=null; var buffer=new byte[65536];
            try { while(!lifetime.IsCancellationRequested) {
                var received=await socket.ReceiveFromAsync(buffer,SocketFlags.None,new IPEndPoint(IPAddress.Any,0),lifetime.Token);
                if(Owned==null) continue;
                var fromOwned=received.RemoteEndPoint.Equals(Owned);
                if(!fromOwned) {
                    if(!((IPEndPoint)received.RemoteEndPoint).Address.Equals(address)) continue;
                    if(browser!=null && !browser.Equals(received.RemoteEndPoint)) continue;
                    browser=(IPEndPoint)received.RemoteEndPoint;
                }
                if(fromOwned && Volatile.Read(ref dropNext)==1 && received.ReceivedBytes>12 && (buffer[0]&0xc0)==0x80 && (buffer[1]&0x7f)==PayloadType) {
                    Interlocked.Exchange(ref dropNext,0);Interlocked.Increment(ref dropped);continue;
                }
                if(fromOwned && browser==null) continue;
                await socket.SendToAsync(buffer.AsMemory(0,received.ReceivedBytes),SocketFlags.None,fromOwned?browser!:Owned,lifetime.Token);
            } } catch(OperationCanceledException) when(lifetime.IsCancellationRequested) {}
        });
    }
    public async ValueTask DisposeAsync(){lifetime.Cancel();await worker;socket.Dispose();lifetime.Dispose();}
}
