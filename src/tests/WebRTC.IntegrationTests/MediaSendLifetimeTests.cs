using System.Buffers.Binary;
using System.Net;
using tryAGI.WebRTC;

internal static class MediaSendLifetimeTests
{
    internal static async Task Exercise(SrtpProfile profile)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        await using var left = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await using var right = new IceUdpTransport(new(IPAddress.Loopback, 0));
        await Task.WhenAll(left.ConnectAsync(right.LocalCredentials, IceRole.Controlling, [new(right.LocalEndPoint)], ct),
            right.ConnectAsync(left.LocalCredentials, IceRole.Controlled, [new(left.LocalEndPoint)], ct));
        using var a = DtlsIdentity.Generate(); using var b = DtlsIdentity.Generate();
        var options = new DtlsSrtpOptions { Profiles = [profile] };
        await using var sender = new DtlsSrtpTransport(left, a, DtlsRole.Client, b.GetFingerprintSha256(), options);
        await using var receiver = new DtlsSrtpTransport(right, b, DtlsRole.Server, a.GetFingerprintSha256(), options);
        await Task.WhenAll(sender.ConnectAsync(ct), receiver.ConnectAsync(ct));
        await using var reader = receiver.ReceiveMediaDatagramsAsync(ct).GetAsyncEnumerator(ct);
        byte[]? retained = null; byte[]? retainedCopy = null;
        ushort sequence = 1;
        for (var n = 0; n < 96; n++)
        {
            // Reuse different pool buckets and lengths; the receiver must never see capacity padding.
            var payloadLength = n % 3 == 0 ? 1 : n % 3 == 1 ? 80 : 1000;
            var packet = new byte[12 + payloadLength]; packet[0] = 0x80; packet[1] = 111;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++); packet[11] = 42;
            packet.AsSpan(12).Fill((byte)n);
            if (n % 8 == 0)
            {
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Reject<OperationCanceledException>(async () => await sender.SendRtpAsync(packet, cancelled.Token));
                await Reject<InvalidOperationException>(async () => await sender.SendRtpAsync(new byte[5], ct));
                await Reject<ArgumentOutOfRangeException>(async () => await sender.SendRtpAsync(new byte[1201], ct));
            }
            await sender.SendRtpAsync(packet, n % 2 == 0 ? CancellationToken.None : ct);
            if (!await reader.MoveNextAsync() || reader.Current.Kind != SecureMediaKind.Rtp ||
                !reader.Current.Data.AsSpan().SequenceEqual(packet)) throw new IOException("Pooled media bytes/length changed");
            retained ??= reader.Current.Data;
            retainedCopy ??= packet.ToArray();
            if (!retained.AsSpan().SequenceEqual(retainedCopy)) throw new IOException("Later send mutated retained media");
            if (n % 8 == 0)
            {
                var rtcp = Convert.FromHexString("80c900010000002a");
                await sender.SendRtcpAsync(rtcp, ct);
                if (!await reader.MoveNextAsync() || reader.Current.Kind != SecureMediaKind.Rtcp ||
                    !reader.Current.Data.AsSpan().SequenceEqual(rtcp)) throw new IOException("Pooled SRTCP bytes changed");
            }
        }
        if (receiver.GetDiagnostics().RejectedRecords != 0) throw new IOException("Pooled sends caused authentication failures");
        // A stalled application reader must cause bounded eviction, never reuse its owned bytes.
        ushort lastSent = 0;
        for (var n = 0; n < 512; n++)
        {
            var packet = new byte[13]; packet[0] = 0x80; packet[1] = 111; packet[11] = 42;
            lastSent = sequence++; BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), lastSent);
            packet[12] = (byte)lastSent;
            await sender.SendRtpAsync(packet, ct);
            if (n % 4 == 0) await Task.Delay(1, ct);
        }
        while (receiver.GetDiagnostics().DroppedMediaDatagrams == 0) await Task.Delay(1, ct);
        for (var n = 0; ; n++)
        {
            if (n > 512 || !await reader.MoveNextAsync() || !RtpPacket.TryParse(reader.Current.Data, out var parsed) ||
                reader.Current.Data.Length != 13 || parsed.Payload[0] != (byte)parsed.SequenceNumber)
                throw new IOException("Slow-reader eviction corrupted pooled delivery");
            if (parsed.SequenceNumber == lastSent) break;
        }
        var pending = new List<Task>();
        for (var n = 0; n < 128; n++)
        {
            var packet = new byte[13]; packet[0] = 0x80; packet[1] = 111; packet[11] = 42;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++);
            pending.Add(ObserveShutdown(sender.SendRtpAsync(packet).AsTask()));
        }
        await sender.DisposeAsync();
        await Task.WhenAll(pending).WaitAsync(ct);
        await Reject<ObjectDisposedException>(async () => await sender.SendRtpAsync(new byte[13], ct));
        if (retained is null || retainedCopy is null || !retained.AsSpan().SequenceEqual(retainedCopy))
            throw new IOException("Disposal mutated caller-owned received media");
    }
    private static async Task ObserveShutdown(Task send)
    {
        try { await send; }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new IOException($"Expected {typeof(T).Name}");
    }
}
