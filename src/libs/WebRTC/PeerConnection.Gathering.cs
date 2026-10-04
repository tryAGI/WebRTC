using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace tryAGI.WebRTC;

public sealed partial class PeerConnection
{
    /// <summary>Explicit UDP STUN URI, bounded DNS and endpoint policy on this peer's owned socket.</summary>
    public Task<IceCandidate> GatherServerReflexiveCandidateAsync(IceServerUri server, IceServerResolutionOptions resolution,
        StunGatheringOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(resolution); resolution.Validate();
        if (server.IsTurn || !server.SupportsGathering) throw new NotSupportedException("This path supports stun/UDP only.");
        options ??= new(); options.Validate();
        return GatherUriAsync(server, resolution, (endpoint, ct) => _ice.GatherServerReflexiveCandidateAsync(endpoint, options, ct), cancellationToken);
    }

    /// <summary>URI-selected UDP/TCP/TLS server transport, with original-host TLS identity and separate credentials.</summary>
    public Task<IceCandidate> GatherRelayCandidateAsync(IceServerUri server, TurnCredentials credentials,
        IceServerResolutionOptions resolution, TurnUdpOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(resolution); resolution.Validate();
        if (!server.IsTurn || !server.SupportsGathering) throw new NotSupportedException("TURN over DTLS is not supported.");
        options ??= new();
        if (server.Transport != TurnServerTransport.Tls && options.Tls != null)
            throw new ArgumentException("TLS options cannot be used with an insecure URI.", nameof(options));
        // The URI is authoritative for both transport and certificate identity. Trust roots/revocation stay caller-owned.
        options = options with { ServerTransport = server.Transport, Tls = server.Transport == TurnServerTransport.Tls ?
            (options.Tls ?? new TurnTlsOptions { ServerName = server.Host }) with { ServerName = server.Host } : null };
        options.Validate();
        return GatherUriAsync(server, resolution, (endpoint, ct) => _ice.GatherRelayCandidateAsync(endpoint, credentials, options, ct), cancellationToken);
    }

    private Task<IceCandidate> GatherUriAsync(IceServerUri server, IceServerResolutionOptions resolution,
        Func<IPEndPoint, CancellationToken, Task<IceCandidate>> gather, CancellationToken cancellationToken) =>
        GatherCandidateAsync(async ownerToken =>
        {
            // Admission is reserved BEFORE DNS and released only when this operation ends.
            var startedAt = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
            deadline.CancelAfter(resolution.GatherTimeout);
            void CheckDeadline()
            {
                ownerToken.ThrowIfCancellationRequested();
                // Timer callbacks can be delayed behind the attempt's timeout continuation.
                // Monotonic elapsed time keeps total-deadline classification consistent.
                if (Stopwatch.GetElapsedTime(startedAt) >= resolution.GatherTimeout)
                    throw new TimeoutException("ICE server gathering exceeded its total deadline.");
                deadline.Token.ThrowIfCancellationRequested();
            }
            try
            {
                var endpoints = await server.ResolveAsync(_endpoint.AddressFamily, resolution, deadline.Token).ConfigureAwait(false);
                Exception? lastFailure = null;
                foreach (var endpoint in endpoints)
                {
                    CheckDeadline();
                    try { return await gather(endpoint, deadline.Token).ConfigureAwait(false); }
                    // Failed allocations already dispose their owners. Identity/auth failures are terminal, never a downgrade.
                    catch (Exception error) when (error is SocketException or TimeoutException)
                    { CheckDeadline(); lastFailure = error; }
                }
                CheckDeadline();
                throw new IOException("All admitted ICE server addresses failed.", lastFailure);
            }
            catch (OperationCanceledException) when (!ownerToken.IsCancellationRequested)
            { throw new TimeoutException("ICE server gathering exceeded its total deadline."); }
        }, cancellationToken);
}
