using tryAGI.WebRTC;

// Intentionally uses the immutable published package, not the source project.
await PeerSctpRoleTests.Exchange(SdpSetup.Active, SctpRole.Responder, expectPassiveTimeout: true);
await PeerSctpRoleTests.Exchange(SdpSetup.Passive, SctpRole.Responder);
Console.WriteLine("Published 0.2.2 baseline reproduction and DTLS-client positive control passed.");

await BundleTransportTests.Exchange(expectLegacyFailure: true);
Console.WriteLine("Published 0.2.2 non-tag BUNDLE ICE/DTLS route regression passed.");
