using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class DtlsReceiveBoundsTests
{
    internal static async Task IceBoundary()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new() { MaximumDataDatagramSize = 4096 });
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        var exact = Enumerable.Repeat((byte)23, 2048).ToArray();
        await left.SendDatagramAsync(exact, ct);
        await foreach (var data in right.ReceiveDatagramsAsync(ct))
        {
            if (!data.AsSpan().SequenceEqual(exact)) throw new IOException("ICE receive boundary changed payload.");
            break;
        }
        var rejected = right.GetDiagnostics().DroppedDatagrams;
        await left.SendDatagramAsync(Enumerable.Repeat((byte)23, 2049).ToArray(), ct);
        while (right.GetDiagnostics().DroppedDatagrams == rejected) await Task.Delay(5, ct);
        await left.SendDatagramAsync("after-oversize"u8.ToArray(), ct);
        await foreach (var data in right.ReceiveDatagramsAsync(ct))
        {
            if (!data.AsSpan().SequenceEqual("after-oversize"u8)) throw new IOException("Oversized ICE data was delivered.");
            break;
        }
        try { await right.SendDatagramAsync(new byte[1201], ct); throw new IOException("Receive limit changed outgoing ICE budget."); }
        catch (ArgumentOutOfRangeException) { }
    }
    internal static async Task Exchange(int bound)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new() { MaximumReceiveDataDatagramSize = 4096 });
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0), options: new() { MaximumReceiveDataDatagramSize = 4096 });
        await using var proxy = new RecordPairProxy(left.LocalEndPoint, right.LocalEndPoint, bound);
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(proxy.EndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(proxy.EndPoint)], ct));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        await using var client = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256());
        await using var server = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(),
            new() { MaximumReceiveDatagramSize = bound });
        await Task.WhenAll(client.ConnectAsync(ct), server.ConnectAsync(ct));
        static byte[] Payload(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
        var first = Payload((bound - 74) / 2, 41); var second = Payload(bound - 74 - first.Length, 82);
        // RFC 6347 permits independently authenticated records concatenated in one datagram.
        await client.SendApplicationDatagramAsync(first, ct); await client.SendApplicationDatagramAsync(second, ct);
        if (!(await Read(server, ct)).AsSpan().SequenceEqual(first) || !(await Read(server, ct)).AsSpan().SequenceEqual(second) || proxy.LastCombinedSize != bound)
            throw new IOException("Exact receive boundary was not admitted.");
        var rejected = server.GetDiagnostics().RejectedRecords;
        await client.SendApplicationDatagramAsync(Payload(first.Length + 1, 123), ct);
        await client.SendApplicationDatagramAsync(second, ct);
        while (server.GetDiagnostics().RejectedRecords == rejected) await Task.Delay(5, ct);
        if (proxy.LastCombinedSize != bound + 1) throw new IOException("Oversize path was not exercised.");
        using (var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await Read(server, quiet.Token); throw new IOException("Part of an oversized datagram was delivered."); }
            catch (OperationCanceledException) when (quiet.IsCancellationRequested) { }
        }
        await proxy.ReplayOversizeSeparatelyAsync(ct);
        if (!(await Read(server, ct)).AsSpan().SequenceEqual(Payload(first.Length + 1, 123)) || !(await Read(server, ct)).AsSpan().SequenceEqual(second))
            throw new IOException("Rejected datagram advanced authenticated record replay state.");
        await client.SendApplicationDatagramAsync("after-bound"u8.ToArray(), ct);
        await client.SendApplicationDatagramAsync("still-authenticated"u8.ToArray(), ct);
        if (!(await Read(server, ct)).AsSpan().SequenceEqual("after-bound"u8) || !(await Read(server, ct)).AsSpan().SequenceEqual("still-authenticated"u8))
            throw new IOException("Oversize input changed the authenticated association.");
        if (client.MaximumApplicationDatagramSize != 1163 || server.MaximumApplicationDatagramSize != 1163)
            throw new IOException("Receive budget changed outgoing fragmentation.");
        foreach (var invalid in new[] { 255, 16385 })
        {
            try
            {
                await using var invalidTransport = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), new() { MaximumReceiveDatagramSize = invalid });
                throw new IOException("Invalid receive budget accepted.");
            }
            catch (ArgumentOutOfRangeException) { }
        }
    }
    private static async Task<byte[]> Read(DtlsSrtpTransport peer, CancellationToken ct)
    {
        await foreach (var data in peer.ReceiveApplicationDatagramsAsync(ct)) return data;
        throw new IOException("Required application record missing.");
    }
    private sealed class RecordPairProxy : IAsyncDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _worker;
        private int _lastCombinedSize;
        private byte[][]? _oversizeRecords;
        private readonly IPEndPoint _right;
        internal async Task ReplayOversizeSeparatelyAsync(CancellationToken ct)
        {
            var records = Volatile.Read(ref _oversizeRecords) ?? throw new IOException("Oversize records not captured.");
            foreach (var record in records) await _socket.SendToAsync(record, SocketFlags.None, _right, ct);
        }
        internal int LastCombinedSize => Volatile.Read(ref _lastCombinedSize);
        internal IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;
        internal RecordPairProxy(IPEndPoint left, IPEndPoint right, int bound)
        {
            _right = right; _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _worker = Task.Run(async () =>
            {
                byte[]? held = null; var buffer = new byte[4096];
                try
                {
                    while (true)
                    {
                        var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token);
                        var fromLeft = received.RemoteEndPoint.Equals(left);
                        if (!fromLeft && !received.RemoteEndPoint.Equals(right)) continue;
                        var data = buffer.AsMemory(0, received.ReceivedBytes).ToArray();
                        if (fromLeft && data[0] == 23)
                        {
                            if (held == null) { held = data; continue; }
                            if (held.Length + data.Length > bound) Volatile.Write(ref _oversizeRecords, new[] { held, data });
                            data = [.. held, .. data]; held = null; Volatile.Write(ref _lastCombinedSize, data.Length);
                        }
                        await _socket.SendToAsync(data, SocketFlags.None, fromLeft ? right : left, _lifetime.Token);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            });
        }
        public async ValueTask DisposeAsync() { _lifetime.Cancel(); await _worker; _socket.Dispose(); _lifetime.Dispose(); }
    }
}
