using System.Net;
using System.Net.Sockets;
using tryAGI.WebRTC;

internal static class UdpReceiveSourceChecks
{
    internal static void Run()
    {
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            var source = new UdpReceiveSource(family);
            var first = new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.Parse("fe80::1%2"), 1234);
            var second = new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Parse("127.0.0.2") : IPAddress.Parse("fe80::1%3"), 5678);
            Set(first); var retained = source.Resolve();
            for (var i = 0; i < 1000; i++) _ = source.Resolve();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++)
                if (!ReferenceEquals(retained, source.Resolve())) throw new IOException("Stable UDP source rematerialized");
            if (GC.GetAllocatedBytesForCurrentThread() != before) throw new IOException("Stable source resolution allocated");
            Set(second); var changed = source.Resolve();
            if (!changed.Equals(second) || !retained.Equals(first) || ReferenceEquals(retained, changed))
                throw new IOException("Reused socket address corrupted retained source identity");
            Set(first); var returned = source.Resolve();
            if (!returned.Equals(first) || !changed.Equals(second)) throw new IOException("Alternating receive source identity changed");

            void Set(IPEndPoint endpoint)
            {
                var serialized = endpoint.Serialize();
                if (serialized.Size != source.Address.Size) throw new IOException("Fixture socket address size mismatch");
                serialized.Buffer.Span.CopyTo(source.Address.Buffer.Span);
            }
        }
    }
}
