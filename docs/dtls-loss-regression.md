# Combined DTLS loss regression

The local UDP integration suite includes six combined-fault cases: every SRTP
profile, both with and without the cookie exchange, at a 256-byte transmit MTU.
The proxy deterministically drops two ClientHellos, one server Certificate
fragment and the first encrypted server Finished, while delivering ServerHello
after a later server handshake fragment. STUN and application traffic are not
randomly impaired. Each planned fault has an asserted counter, so a passing test
cannot silently skip the fault path.

After both endpoints connect, the existing exchange verifies authenticated
application data and bidirectional SRTP/SRTCP. This checks loss recovery without
relaxing certificate fingerprints, record authentication or replay protection.
It does not identify the cause of an intermittent external-provider timeout.

Run the focused cases after building:

```sh
dotnet run --project src/tests/WebRTC.IntegrationTests -c Release --no-build -- --case-filter "DTLS combined"
```

The shared M4 host rejected the local build at compiler admission with
`M4 requires primary release sources`; no compiler ran and no admission bypass
was attempted. Validation must come from the ordinary native CI matrix until
that host admits this repository. The change contains tests and documentation
only; it does not modify the protocol implementation or package version.
