# Explicit provider ICE server URIs

`IceServerUri` implements a bounded, explicitly supported subset of the opaque
STUN/TURN URI grammars in [RFC7064](https://www.rfc-editor.org/rfc/rfc7064.html)
and [RFC7065](https://www.rfc-editor.org/rfc/rfc7065.html). It does not use a
hierarchical HTTP URI parser. Credentials are separate `TurnCredentials`, never
userinfo, URI query parameters or returned diagnostic text.

Supported input hosts are ASCII DNS labels (including already converted IDNA
A-labels), canonical dotted IPv4, and bracketed IPv6 without a scope identifier.
URI input is capped at 512 characters, DNS names at 253, labels at 63, and ports
at 1–65535. Paths, fragments, userinfo, escapes, control/Unicode characters,
ambiguous IPv4 forms and extra query parameters are rejected. This deliberately
restricts the more general registered-name grammar; callers must not reinterpret
rejected input using a different parser.

| URI | Default port | Gathering |
| --- | --- | --- |
| `stun:host[:port]` | 3478 | Same-socket UDP Binding |
| `stuns:host[:port]` | 5349 | Parsed; secure STUN not implemented |
| `turn:host[:port]` or `?transport=udp` | 3478 | UDP control, UDP relay |
| `turn:host[:port]?transport=tcp` | 3478 | TCP control, UDP relay |
| `turns:host[:port]` or `?transport=tcp` | 5349 | TLS control, UDP relay |
| `turns:host[:port]?transport=udp` | 5349 | Parsed; TURN over DTLS not implemented |

Unsupported protected transports fail before resolution or server contact. They
never downgrade. Only `udp` and `tcp` transport tokens are accepted. The URI is
authoritative for TURN control transport; relay transport remains UDP.

## Resolution and admission

`ResolveAsync(localAddressFamily, resolutionOptions, token)` uses the .NET system
resolver's cancellable A/AAAA API once, matching the already selected local
interface family. Literal addresses bypass DNS. The raw returned inventory is
capped at eight addresses by default (configurable 1–16); an oversized inventory
fails rather than silently ignoring its tail. Unicast candidate validation,
deduplication and the **required** application `EndpointFilter` apply before any
STUN/TURN packet or connection. The predicate receives a deep copy so mutation
cannot redirect the pinned destination. Predicates must be pure, fast and nonblocking.

Resolution defaults to three seconds (100 ms–15 s). URI gathering has a separate
**total** 45-second default deadline (100 ms–2 min) covering resolution and all
sequential address attempts. It reserves the existing eight-candidate peer budget
before resolution and refuses completed gathering. Cancellation/disposal removes
active transactions; previously attached paths remain under their existing owner.
Socket failures and transaction/connect timeouts permit the next admitted address
in the same pinned list. Protocol, policy, credential and TLS authentication
failures stop the operation. There is no alternate hostname lookup or transport downgrade.

`turns:` validates the certificate against the **original URI host**, never the
resolved IP or a caller's replacement `TurnTlsOptions.ServerName`. Callers may
supply the existing explicit roots/revocation policy. Default system trust remains
available. Credentials are transmitted only after TLS authentication succeeds.

```csharp
var resolution = new IceServerResolutionOptions
{
    // Define an application-specific policy over actual resolved addresses.
    EndpointFilter = endpoint => approvedServerAddresses.Contains(endpoint.Address),
};
var server = IceServerUri.Parse(providerUrl);
var relay = await peer.GatherRelayCandidateAsync(
    server, new TurnCredentials(username, password), resolution,
    cancellationToken: cancellationToken);
```

The resolved `IPEndPoint` gathering overloads remain available. Their callers own
resolution and server admission; the remote ICE `CandidateFilter` and TURN
`PeerFilter` are separate policies and do not approve a server automatically.

## Evidence and remaining scope

Protocol cases cover standards URI examples, canonical round trips, malformed and
ambiguous addresses, bounds and a deterministic hostile corpus. Local network
cases use only `localhost`/loopback: IPv4/IPv6 Binding from the owned socket,
endpoint-policy rejection/mutation, completed and saturated admission,
cancellation/total deadline/disposal, real UDP/TCP/TLS TURN authentication and
allocation deletion. A locally issued TLS certificate verifies both original-host
success and identity rejection **before credentials**. The whole-rooted native
smoke executes these same cases. These are provider-address prerequisites, not
provider session or physical Watch acceptance.

No SRV/NAPTR discovery from RFC5928, explicit mDNS discovery, Happy Eyeballs,
automatic interface selection, ICE restart or real NAT lifecycle matrix is
implemented here. The OS resolver may use its configured host-resolution mechanisms.
DNS response validation/trust remains the operating system/application policy;
endpoint admission does not establish the ownership of a hostname.
