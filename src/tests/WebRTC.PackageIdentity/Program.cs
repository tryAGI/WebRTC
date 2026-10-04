// This executable is built against the actual nupkg DLL by verify-package-identity.py.
// No reflection, assembly-name inference, source rebuild or provider connection is used.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBRTC_EXPECTED_PACKAGE_VERSION")) ||
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBRTC_EXPECTED_SOURCE_REVISION")))
{
    Console.Error.WriteLine("Exact expected package version and source revision are required.");
    return 2;
}
try
{
    await BuildIdentityTests.Retained();
    Console.WriteLine("PASS packaged library identity and retained establishment evidence");
    return 0;
}
catch (IOException)
{
    Console.Error.WriteLine("FAIL packaged library identity differs from the verified package manifest.");
    return 1;
}
