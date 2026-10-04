namespace tryAGI.WebRTC;

/// <summary>Build-time identity of the loaded library, not its caller or AssemblyVersion.
/// An empty SourceRevisionId means that no repository revision was available to the build.
/// These values identify build inputs; they do not attest to a clean tree or authentic binary.</summary>
public sealed record WebRtcBuildIdentity(string PackageId, string PackageVersion, string SourceRevisionId);

/// <summary>Generated build inputs accessible without reflection on managed, trimmed and NativeAOT runtimes.</summary>
public static partial class WebRtcBuildInfo
{
    // Do not use public constants: a consumer must observe the loaded library, not an inlined old value.
    public static WebRtcBuildIdentity Current { get; } = Create();
    private static partial WebRtcBuildIdentity Create();
}
