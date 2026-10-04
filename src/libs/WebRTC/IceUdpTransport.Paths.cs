using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public sealed partial class IceUdpTransport
{
    private const int MaximumRelayPaths = 3;
    private readonly LocalPath _hostPath;
    private readonly List<LocalPath> _paths = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly HashSet<Task<IceCandidate>> _relayGathers = [];
    private int _activeRelayGathers;

    /// <summary>
    /// Allocates an explicitly resolved UDP TURN server on a separate owned socket of this interface.
    /// Up to three allocations may join this generation, including after ConnectAsync starts.
    /// Canceling an unattached gather releases only that allocation; disposal joins all owned workers.
    /// </summary>
    public Task<IceCandidate> GatherRelayCandidateAsync(IPEndPoint server, TurnCredentials credentials,
        TurnUdpOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var destination = new IceCandidate(server).EndPoint;
        var local = LocalEndPoint;
        if (destination.AddressFamily != local.AddressFamily) throw new ArgumentException("TURN server and interface families must match.", nameof(server));
        options ??= new();
        var datagramBound = Math.Max(2048, Math.Max(_options.MaximumDataDatagramSize, _options.MaximumReceiveDataDatagramSize));
        if (options.MaximumDatagramSize < datagramBound)
            options = options with { MaximumDatagramSize = datagramBound };
        lock (_gate)
        {
            ThrowIfStopped();
            if (_paths.Count - 1 + _activeRelayGathers >= MaximumRelayPaths)
                throw new InvalidOperationException("The three owned relay allocation slots are reserved.");
            _relayGathers.RemoveWhere(t => t.IsCompleted);
            _activeRelayGathers++;
            var relayOptions = options;
            var task = Task.Run(() => GatherRelayCoreAsync(new(local.Address, 0), destination, credentials, relayOptions, cancellationToken));
            _relayGathers.Add(task);
            return task;
        }
    }

    private async Task<IceCandidate> GatherRelayCoreAsync(IPEndPoint local, IPEndPoint server, TurnCredentials credentials,
        TurnUdpOptions options, CancellationToken cancellationToken)
    {
        TurnUdpAllocation? allocation = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            allocation = await TurnUdpAllocation.AllocateAsync(local, server, credentials, options with { DiagnosticTransport = this }, lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            var path = new LocalPath(allocation.Candidate, allocation);
            lock (_gate)
            {
                ThrowIfStopped();
                lifetime.Token.ThrowIfCancellationRequested();
                if (_pairs.Count + _remoteCandidates.Count > _options.MaximumCandidatePairs)
                    throw new InvalidOperationException("Attaching the relay would exceed the ICE pair budget.");
                foreach (var candidate in _remoteCandidates) AddCandidateCore(path, candidate);
                _paths.Add(path);
                path.PermissionsWorker = Task.Run(() => PermissionLoopAsync(path));
                path.Reader = Task.Run(() => RelayReceiveLoopAsync(path));
                allocation = null; // Ownership transfers atomically with the attached path.
            }
            WakeChecker();
            return path.Candidate;
        }
        finally
        {
            try { if (allocation is not null) await allocation.DisposeAsync().ConfigureAwait(false); }
            finally { lock (_gate) _activeRelayGathers--; }
        }
    }

    // All permission bookkeeping is under the ICE gate. TURN controls always run outside it.
    private bool PermissionReady(LocalPath path, IPEndPoint peer) => path.Relay is null || path.ReadyPermissions.Contains(peer.Address.ToString());
    private void QueuePermission(LocalPath path, IPEndPoint peer)
    {
        if (path.Relay is null) return;
        var ip = peer.Address.ToString();
        if (path.ReadyPermissions.Contains(ip) || path.DeniedPermissions.Contains(ip) || !path.PendingPermissions.Add(ip)) return;
        if (!path.PermissionQueue.Writer.TryWrite(peer))
        { path.PendingPermissions.Remove(ip); throw new InvalidOperationException("Relay permission queue is full."); }
    }
    private async Task PermissionLoopAsync(LocalPath path)
    {
        try
        {
            await foreach (var peer in path.PermissionQueue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var ip = peer.Address.ToString();
                try
                {
                    await path.Relay!.CreateOwnedPermissionAsync(peer, _lifetime.Token).ConfigureAwait(false);
                    lock (_gate) { path.PendingPermissions.Remove(ip); path.ReadyPermissions.Add(ip); }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    lock (_gate)
                    {
                        path.PendingPermissions.Remove(ip); path.DeniedPermissions.Add(ip);
                        foreach (var pair in _pairs.Where(p => p.Path == path && p.Candidate.TransportEndPoint.Address.Equals(peer.Address)))
                            pair.Failed = true;
                    }
                }
                WakeChecker();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { FailPath(path, error); }
    }
    private async Task RelayReceiveLoopAsync(LocalPath path)
    {
        try
        {
            await foreach (var packet in path.Relay!.ReceiveDatagramsAsync(_lifetime.Token).ConfigureAwait(false))
                await HandleDatagramAsync(path, packet.Data, packet.Source, packet.Trace).ConfigureAwait(false);
            if (!_lifetime.IsCancellationRequested)
                FailPath(path, await path.Relay.Completion.ConfigureAwait(false) ?? new IOException("Owned relay closed."));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) FailPath(path, error); }
    }

    private async ValueTask HandleDatagramAsync(LocalPath path, ReadOnlyMemory<byte> packet, IPEndPoint source, PacketDiagnostic trace = default)
    {
        // RFC 7983 content-type classification is evidence of framing, not authentication.
        if (packet.Length != 0 && packet.Span[0] is >= 20 and <= 63) trace.Protocol(DiagnosticProtocol.Dtls);
        trace.Mark(PacketStage.Demultiplexed);
        if (packet.Length == 0) return;
        if (packet.Span[0] <= 3)
        {
            byte[]? response;
            lock (_gate)
                response = path == _hostPath && HandleGatheringResponse(packet.Span, source) ? null : HandleStun(path, packet.Span, source);
            if (response is not null) await TrySendPathAsync(path, response, source, _lifetime.Token).ConfigureAwait(false);
            WakeChecker();
        }
        else
        {
            lock (_gate)
            {
                var rejection = DataAdmissionRejection(path, source, packet.Length);
                if (rejection is null)
                {
                    trace.Mark(PacketStage.IceEnqueued, queueDepth: Math.Min(_options.ReceiveQueueCapacity, _datagrams.Reader.Count + 1));
                    _datagrams.Writer.TryWrite(new(packet.ToArray(), trace));
                }
                else
                {
                    _dataRejections[(int)rejection.Value]++;
                    Interlocked.Increment(ref _droppedDatagrams);
                    // Keep the existing trace category; precise counters have an independent retained API.
                    trace.Mark(PacketStage.Dropped, PacketReason.InvalidRouteOrConsent);
                }
            }
        }
    }
    // Caller holds _gate. Preserve admission checks and their original short-circuit order.
    private IceDatagramRejectionReason? DataAdmissionRejection(LocalPath path, IPEndPoint source, int bytes)
    {
        if (_stopped) return IceDatagramRejectionReason.TransportStopped;
        if (!PathAllowed(path)) return IceDatagramRejectionReason.LocalPathUnavailable;
        if (_selected is null) return IceDatagramRejectionReason.NoNominatedPair;
        if (path != _selected.Path) return IceDatagramRejectionReason.PathMismatch;
        if (!source.Equals(_selected.Candidate.TransportEndPoint)) return IceDatagramRejectionReason.SourceMismatch;
        if (bytes > _options.MaximumReceiveDataDatagramSize) return IceDatagramRejectionReason.Oversized;
        if (Elapsed(_lastConsentAt) >= _options.ConsentTimeout) return IceDatagramRejectionReason.ConsentExpired;
        return null;
    }
    private async ValueTask SendPathAsync(LocalPath path, ReadOnlyMemory<byte> packet, IPEndPoint peer, CancellationToken ct, PacketDiagnostic trace = default)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (!PathAllowed(path)) throw new IOException("The local ICE path is unavailable.");
            // An authenticated incoming check may precede the permission worker.
            // Do not send or fail the path until it is ready; the check will be retried.
            if (!PermissionReady(path, peer)) return;
        }
        try
        {
            if (path.Relay is null)
            {
                trace.Mark(PacketStage.SocketSendStarted);
                await _socket.SendToAsync(packet, SocketFlags.None, peer, ct).ConfigureAwait(false);
                trace.Mark(PacketStage.SocketSendCompleted);
            }
            else await path.Relay.SendDiagnosticDatagramAsync(peer, packet, ct, trace).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (path.Relay is not null) { FailPath(path, error); throw; }
    }
    private async ValueTask TrySendPathAsync(LocalPath path, ReadOnlyMemory<byte> packet, IPEndPoint peer, CancellationToken ct)
    {
        try { await SendPathAsync(path, packet, peer, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SocketException error) when (IsRemoteNetworkError(error)) { }
        catch (Exception) when (path.Relay is not null && !_lifetime.IsCancellationRequested) { }
    }
    private void FailPath(LocalPath path, Exception reason)
    {
        bool selected;
        lock (_gate)
        {
            if (path.Failed || _stopped) return;
            path.Failed = true;
            path.PermissionQueue.Writer.TryComplete();
            path.PendingPermissions.Clear();
            foreach (var pair in _pairs.Where(p => p.Path == path))
            {
                pair.Failed = true;
                if (pair.Active is { } active) _transactions.Remove(active.Id);
                pair.Active = null;
            }
            _earlyChecks.RemoveAll(c => c.Path == path);
            selected = _selected?.Path == path;
        }
        if (selected) Stop(new IOException("The nominated local relay failed; its authentication cannot be reused on another path.", reason));
        WakeChecker();
    }
    private async Task DisposePathsAsync()
    {
        Task<IceCandidate>[] gathers;
        lock (_gate) gathers = _relayGathers.ToArray();
        foreach (var gather in gathers) { try { await gather.ConfigureAwait(false); } catch (Exception) { /* Caller retains the gather failure. */ } }
        LocalPath[] paths;
        lock (_gate) paths = _paths.Where(p => p.Relay is not null).ToArray();
        await Task.WhenAll(paths.SelectMany(p => new[] { p.Reader, p.PermissionsWorker })).ConfigureAwait(false);
        foreach (var path in paths) await path.Relay!.DisposeAsync().ConfigureAwait(false);
    }
    private sealed class LocalPath(IceCandidate candidate, TurnUdpAllocation? relay)
    {
        public IceCandidate Candidate { get; } = candidate;
        public TurnUdpAllocation? Relay { get; } = relay;
        public bool Failed;
        public HashSet<string> ReadyPermissions { get; } = [];
        public HashSet<string> PendingPermissions { get; } = [];
        public HashSet<string> DeniedPermissions { get; } = [];
        public Channel<IPEndPoint> PermissionQueue { get; } = Channel.CreateBounded<IPEndPoint>(new BoundedChannelOptions(64)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        public Task Reader { get; set; } = Task.CompletedTask;
        public Task PermissionsWorker { get; set; } = Task.CompletedTask;
    }
}
