using System.Net;
using tryAGI.WebRTC;

internal static class BuildIdentityTests
{
    internal static async Task Retained()
    {
        var build = WebRtcBuildInfo.Current;
        if (build.PackageId != "tryAGI.WebRTC" || !build.PackageVersion.StartsWith("0.", StringComparison.Ordinal) ||
            build.PackageVersion.Length > 128 || build.PackageVersion.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+')) ||
            build.SourceRevisionId.Length is not (0 or 40 or 64) || build.SourceRevisionId.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("Invalid build-time library identity");
        foreach (var (variable, actual) in new[]
        {
            ("WEBRTC_EXPECTED_PACKAGE_VERSION", build.PackageVersion),
            ("WEBRTC_EXPECTED_SOURCE_REVISION", build.SourceRevisionId),
        })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } expected && actual != expected)
                throw new IOException($"Library build identity does not match {variable}");
        await using var peer = new PeerConnection(new() { LocalEndPoint = new(IPAddress.Loopback, 0) });
        var before = peer.GetEstablishmentEvidence();
        await peer.DisposeAsync();
        var after = peer.GetEstablishmentEvidence();
        if (before.Build != build || after.Build != build || before.Build != after.Build)
            throw new IOException("Establishment evidence lost or changed the loaded library identity");
        Console.WriteLine($"Loaded {build.PackageId} {build.PackageVersion}, source {build.SourceRevisionId}");
    }
}
