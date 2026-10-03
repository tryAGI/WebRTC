using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class VideoNetworkTests
{
    private static void Check(bool result) { if (!result) throw new IOException("Authenticated video network assertion failed"); }
    internal static async Task Local(VideoCodec codec, SrtpProfile profile, bool loss)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0)); await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var proxy = new VideoDatagramProxy(left.LocalEndPoint, right.LocalEndPoint);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(proxy.EndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(proxy.EndPoint)], ct));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        var options = new DtlsSrtpOptions { Profiles = [profile] };
        await using var sender = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256(), options);
        await using var receiver = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), options);
        await Task.WhenAll(sender.ConnectAsync(ct), receiver.ConnectAsync(ct));
        await Exchange(sender, receiver, codec, loss, ct, proxy);
    }
    internal static async Task Exchange(DtlsSrtpTransport sender, DtlsSrtpTransport receiver, VideoCodec codec, bool loss, CancellationToken ct, VideoDatagramProxy? proxy = null)
    {
        var source = 42u + (uint)codec;
        using var assembler = new VideoFrameAssembler(VideoTests.Options(codec) with { SynchronizationSource = source });
        byte[] Packet(ushort seq, uint timestamp, bool marker, byte[] payload) => VideoTests.Packet(seq, timestamp, marker, payload, source);
        ushort sequence = 65534;
        // A prior authenticated packet establishes the SRTP ROC before deliberately reversing ciphertext across rollover.
        var bootstrap = codec == VideoCodec.H264 ? new byte[] { 0x61, 1 } : new byte[] { 0x10, 1, 2, 3 };
        await sender.SendRtpAsync(Packet(sequence++, 1, true, bootstrap), ct);
        Check(assembler.Push((await Read(receiver, ct)).Data).Count == (codec == VideoCodec.H264 ? 0 : 1));
        byte[][] fragments = codec == VideoCodec.H264 ? [[0x7C, 0x85, 11], [0x7C, 5, 22], [0x7C, 0x45, 33]] :
            [[0x10, 1, 2, 3, 11], [0, 22], [0, 33]];
        var first = sequence;
        var frames = new List<EncodedVideoFrame>();
        proxy?.Arm(loss);
        for (var i = 0; i < 3; i++)
            await sender.SendRtpAsync(Packet(unchecked((ushort)(first + i)), 90000, i == 2, fragments[i]), ct);
        for (var i = 0; i < (loss ? 2 : 3); i++) frames.AddRange(assembler.Push((await Read(receiver, ct)).Data));
        if (loss)
        {
            Check(frames.Count == 0);
            var next = codec == VideoCodec.H264 ? new byte[] { 0x65, 44 } : new byte[] { 0x10, 1, 2, 3, 44 };
            await sender.SendRtpAsync(Packet(unchecked((ushort)(first + 3)), 93000, true, next), ct);
            frames.AddRange(assembler.Push((await Read(receiver, ct)).Data));
            Check(frames.Count == 1 && frames[0].Timestamp == 93000 && assembler.GetDiagnostics().DroppedFrames > 0);
            await proxy!.ReleaseLateAsync(ct);
            Check(assembler.Push((await Read(receiver, ct)).Data).Count == 0);
        }
        else
        {
            Check(frames.Count == 1 && frames[0].Timestamp == 90000);
            var expected = codec == VideoCodec.H264 ? new byte[] { 0, 0, 0, 1, 0x65, 11, 22, 33 } : new byte[] { 1, 2, 3, 11, 22, 33 };
            Check(frames[0].Payload.SequenceEqual(expected) && frames[0].FirstSequenceNumber == first && frames[0].LastSequenceNumber == unchecked((ushort)(first + 2)));
        }
        Check(assembler.GetDiagnostics().BufferedBytes == 0);
        Check(sender.GetDiagnostics().Profile == receiver.GetDiagnostics().Profile);
        if (proxy != null) Check(proxy.Affected == 3);
    }
    private static async Task<SecureMediaDatagram> Read(DtlsSrtpTransport receiver, CancellationToken ct)
    {
        await foreach (var packet in receiver.ReceiveMediaDatagramsAsync(ct)) { Check(packet.Kind == SecureMediaKind.Rtp); return packet; }
        throw new IOException("Required authenticated video RTP did not arrive");
    }
}

// Fault injection occurs after SRTP encryption: the production sender never reuses a nonce or reorders packet indices.
internal sealed class VideoDatagramProxy : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private readonly IPEndPoint _right;
    private volatile bool _armed, _loss;
    private byte[]? _late;
    private int _affected;
    internal int Affected => Volatile.Read(ref _affected);
    internal IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    internal VideoDatagramProxy(IPEndPoint left, IPEndPoint right)
    {
        _right = right; _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _worker = Task.Run(async () =>
        {
            var held = new List<byte[]>(); var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                    var fromLeft = received.RemoteEndPoint.Equals(left);
                    if (!fromLeft && !received.RemoteEndPoint.Equals(right)) continue;
                    var bytes = buffer.AsMemory(0, received.ReceivedBytes).ToArray();
                    if (_armed && fromLeft && bytes.Length >= 12 && bytes[0] >> 6 == 2 && (bytes[1] & 127) == 96)
                    {
                        held.Add(bytes); Interlocked.Increment(ref _affected);
                        if (held.Count != 3) continue;
                        _armed = false;
                        if (_loss) _late = held[1];
                        foreach (var i in _loss ? new[] { 0, 2 } : new[] { 2, 0, 1 })
                            await _socket.SendToAsync(held[i], SocketFlags.None, right, _lifetime.Token);
                        held.Clear(); continue;
                    }
                    await _socket.SendToAsync(bytes, SocketFlags.None, fromLeft ? right : left, _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    internal void Arm(bool loss) { _loss = loss; _armed = true; }
    internal async Task ReleaseLateAsync(CancellationToken ct)
    { var packet = Interlocked.Exchange(ref _late, null) ?? throw new IOException("No held encrypted video packet"); await _socket.SendToAsync(packet, SocketFlags.None, _right, ct); }
    public async ValueTask DisposeAsync()
    { _lifetime.Cancel(); await _worker; _socket.Dispose(); _lifetime.Dispose(); }
}
