// The fixture must fail for these exact reasons against the immutable package,
// and pass against current source in the managed and NativeAOT suites.
try
{
    await IceConsentTests.PreviousTransactionResponse();
    throw new InvalidOperationException("Published 0.2.9 unexpectedly accepted previous consent transactions.");
}
catch (InvalidOperationException error) when (error.Message == "Consent expired despite authenticated replies to previous requests.")
{
    Console.WriteLine("Published 0.2.9 premature consent expiry reproduced.");
}
try
{
    await IceConsentTests.RevocationStopsImmediately();
    throw new InvalidOperationException("Published 0.2.9 unexpectedly honored immediate consent revocation.");
}
catch (TimeoutException)
{
    Console.WriteLine("Published 0.2.9 ignored authenticated consent revocation reproduced.");
}
