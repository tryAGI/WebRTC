using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using tryAGI.WebRTC;

var suite = args.Length == 0 ? "crypto" : args.Length == 2 && args[0] == "--suite" ? args[1] : throw new ArgumentException("Use --suite crypto|packets|media|video|channels|cold|soak|audio");
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"cpu_count={Environment.ProcessorCount}; server_gc={System.Runtime.GCSettings.IsServerGC}; stopwatch_hz={Stopwatch.Frequency}; dynamic_code={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled}");
Console.WriteLine($"library_package={WebRtcBuildInfo.Current.PackageVersion}; library_source={WebRtcBuildInfo.Current.SourceRevisionId}");
Console.WriteLine($"harness_source={Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "working_tree_unverified"}");
if (suite == "cold") { await LifecycleBenchmarks.Cold(); return; }
if (suite == "soak") { await LifecycleBenchmarks.Soak(); return; }
if (suite == "audio") { await LifecycleBenchmarks.Audio(); return; }
if (suite == "packets") { PacketBenchmarks.Run(); return; }
if (suite == "media") { await MediaBenchmarks.Run(); return; }
if (suite == "video") { VideoBenchmarks.Run(); return; }
if (suite == "channels") { await ChannelBenchmarks.Run(); return; }
if (suite != "crypto") throw new ArgumentException("Unknown suite");
Console.WriteLine("profile,payload_bytes,sample,roundtrips,managed_bytes_per_roundtrip,ns_per_roundtrip");
foreach (var profile in Enum.GetValues<SrtpProfile>())
foreach (var payload in new[] { 60, 1200 })
{
    var key = new byte[profile == SrtpProfile.AeadAes256Gcm ? 32 : 16];
    var salt = new byte[profile == SrtpProfile.Aes128CmHmacSha1_80 ? 14 : 12];
    // Synthetic keys are used only within this process. Each context has fresh sequence space.
    Random.Shared.NextBytes(key); Random.Shared.NextBytes(salt);
    using var sender = new SrtpContext(profile, SrtpDirection.Send, key, salt);
    using var receiver = new SrtpContext(profile, SrtpDirection.Receive, key, salt);
    var plain = new byte[12 + payload]; plain[0] = 0x80; plain[1] = 111;
    BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(8), 42);
    var encrypted = new byte[plain.Length + sender.RtpOverhead];
    var decrypted = new byte[plain.Length];
    var sequence = 0;
    Roundtrips(1000);
    for (var sample = 0; sample < 7; sample++)
    {
        const int count = 5000;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        Roundtrips(count);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"{profile},{payload},{sample},{count},{bytes / (double)count:F2},{elapsed.TotalNanoseconds / count:F1}");
    }
    void Roundtrips(int count)
    {
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(2), checked((ushort)sequence++));
            if (!sender.TryProtectRtp(plain, encrypted, out var protectedLength) ||
                !receiver.TryUnprotectRtp(encrypted.AsSpan(0, protectedLength), decrypted, out var length) ||
                length != plain.Length || !plain.AsSpan().SequenceEqual(decrypted))
                throw new InvalidOperationException("Authenticated SRTP roundtrip failed.");
        }
    }
}
