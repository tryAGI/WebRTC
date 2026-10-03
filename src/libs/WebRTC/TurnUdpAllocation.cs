using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public sealed record TurnUdpOptions
{
    public StunGatheringOptions Transactions { get; init; } = new();
    public uint RequestedLifetimeSeconds { get; init; } = 600;
    public int MaximumPeers { get; init; } = 32;
    public int MaximumDatagramSize { get; init; } = 1200;
    public int ReceiveQueueCapacity { get; init; } = 32;
    public bool AllowLegacyAuthentication { get; init; } = true;
    /// <summary>Pure, fast admission predicate for outbound peers and indicated inbound sources.</summary>
    public Func<IPEndPoint, bool>? PeerFilter { get; init; }
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Transactions); Transactions.Validate();
        if (RequestedLifetimeSeconds is < 1 or > 3600 || MaximumPeers is < 1 or > 64 ||
            MaximumDatagramSize is < 256 or > 16384 || ReceiveQueueCapacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(TurnUdpOptions));
    }
}
/// <summary>Relay-sourced bytes, NOT peer-authenticated media. Layer ICE/DTLS/SRTP above this path.</summary>
public sealed class TurnDatagram
{
    private readonly IPEndPoint _source;
    public IPEndPoint Source => new IceCandidate(_source).EndPoint;
    public byte[] Data { get; }
    internal TurnDatagram(IPEndPoint source, byte[] data) { _source = new IceCandidate(source).EndPoint; Data = data; }
}
public sealed record TurnUdpDiagnostics(bool AllocationActive, int Permissions, int Channels, TimeSpan RemainingLifetime,
    long SentRequests, long Retransmissions, long RejectedPackets, long DroppedDatagrams, long ReceivedDatagrams,
    bool ModernIntegrity, bool GracefulReleaseAcknowledged);

/// <summary>One owned resolved-server UDP TURN allocation. No DNS, TCP/TLS or ICE pair integration.</summary>
public sealed class TurnUdpAllocation : IAsyncDisposable
{
    private const ushort Realm = 0x0014, Nonce = 0x0015, PasswordAlgorithms = 0x8002, PasswordAlgorithm = 0x001D;
    private readonly object _gate = new();
    private readonly Socket _socket;
    private readonly IPEndPoint _server, _local;
    private readonly TurnUdpOptions _options;
    private readonly SemaphoreSlim _operation = new(1, 1), _maintenanceWake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new(), _maintenanceLifetime = new();
    private readonly Channel<TurnDatagram> _incoming;
    private readonly Task _reader;
    private Task? _maintenance, _dispose;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, Permission> _permissions = [];
    private readonly Dictionary<string, Binding> _bindings = [];
    private ushort _nextChannel = 0x4000;
    private Pending? _pending;
    private bool _stopped, _disposing, _allocated, _modern, _released;
    private long _expires, _refreshAt, _sent, _retries, _rejected, _dropped, _received;
    private byte[] _key = [], _algorithms = [], _username = [], _userhash = [];
    private string? _realm, _nonce;
    private ushort _algorithm;
    private IceCandidate? _candidate;
    private IPEndPoint? _mapped;
    public IceCandidate Candidate => _candidate ?? throw new InvalidOperationException("Allocation not established.");
    public IPEndPoint LocalEndPoint => new IceCandidate(_local).EndPoint;
    public IPEndPoint MappedEndPoint => new IceCandidate(_mapped ?? throw new InvalidOperationException("Allocation not established.")).EndPoint;
    public Task<Exception?> Completion => _completion.Task;
    private static long Now => Stopwatch.GetTimestamp();
    private static long After(double seconds) => Now + (long)(seconds * Stopwatch.Frequency);

    private TurnUdpAllocation(IPEndPoint local, IPEndPoint server, TurnUdpOptions options)
    {
        _options = options; _server = new IceCandidate(server).EndPoint;
        _ = new IceCandidate(new(local.Address, local.Port == 0 ? 1 : local.Port));
        if (local.AddressFamily != server.AddressFamily) throw new ArgumentException("TURN server and local base families must match.");
        _socket = new(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try { _socket.Bind(new IPEndPoint(local.Address, local.Port)); _local = new IceCandidate((IPEndPoint)_socket.LocalEndPoint!).EndPoint; }
        catch { _socket.Dispose(); throw; }
        _incoming = Channel.CreateBounded<TurnDatagram>(new BoundedChannelOptions(options.ReceiveQueueCapacity)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = false }, _ => Interlocked.Increment(ref _dropped));
        _reader = ReadAsync();
    }
    public static async Task<TurnUdpAllocation> AllocateAsync(IPEndPoint local, IPEndPoint server, TurnCredentials credentials,
        TurnUdpOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local); ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(credentials);
        options ??= new(); options.Validate();
        var allocation = new TurnUdpAllocation(local, server, options);
        try
        {
            await allocation._operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var response = await allocation.ControlAsync(3, null, null, credentials, cancellationToken).ConfigureAwait(false);
                allocation.AcceptAllocation(response);
            }
            finally { allocation._operation.Release(); }
            allocation._maintenance = allocation.MaintainAsync(); return allocation;
        }
        catch { await allocation.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private void AcceptAllocation(byte[] bytes)
    {
        if (!StunMessage.TryParse(bytes, out var message)) throw new InvalidDataException("Invalid TURN allocation response.");
        lock (_gate)
        {
            _allocated = true; // A successful remote allocation must be released even if its address is unsupported.
            if (!message.TryGetXorEndpoint(0x0016, out var relay) || !message.TryGetXorMappedEndpoint(out var mapped) ||
                relay!.AddressFamily != _local.AddressFamily || mapped!.AddressFamily != _local.AddressFamily)
                throw new InvalidDataException("Unsupported TURN allocation address family or missing address.");
            _mapped = new IceCandidate(mapped!).EndPoint;
            _candidate = new(relay!, 16777215, IceCandidateType.Relay, _mapped);
            AcceptLifetime(message);
        }
    }
    private void AcceptLifetime(StunMessage message)
    {
        if (!message.TryGetUniqueAttribute(0x000D, out var value) || value.Length != 4) throw new InvalidDataException("Missing TURN lifetime.");
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(value);
        if (seconds is < 1 or > 86400) throw new InvalidDataException("Unsupported TURN allocation lifetime.");
        _expires = After(seconds); _refreshAt = _expires - (long)(Math.Min(60, seconds * .2) * Stopwatch.Frequency);
        if (_maintenanceWake.CurrentCount == 0) _maintenanceWake.Release();
    }
    public TurnUdpDiagnostics GetDiagnostics()
    {
        lock (_gate) return new(!_stopped && _allocated && Now < _expires, _permissions.Count, _bindings.Count,
            TimeSpan.FromSeconds(Math.Max(0, (_expires - Now) / (double)Stopwatch.Frequency)), _sent, _retries,
            _rejected, Interlocked.Read(ref _dropped), _received, _modern, _released);
    }
    private void RequireOpen()
    {
        if (_stopped || _disposing) throw new ObjectDisposedException(nameof(TurnUdpAllocation));
        if (!_allocated || Now >= _expires) throw new IOException("TURN allocation is absent or expired.");
    }
    private IPEndPoint Admit(IPEndPoint peer)
    {
        var safe = new IceCandidate(peer).EndPoint;
        if (safe.AddressFamily != _local.AddressFamily || !(_options.PeerFilter?.Invoke(new IceCandidate(safe).EndPoint) ?? true))
            throw new ArgumentException("TURN peer rejected by destination policy.", nameof(peer));
        return safe;
    }
    private async Task EnterAsync(CancellationToken ct)
    {
        if (!await _operation.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("A TURN control operation is already active.");
        try { lock (_gate) RequireOpen(); } catch { _operation.Release(); throw; }
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _operation.Release(); }
    }
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var bytes = await ControlAsync(4, null, null, null, ct).ConfigureAwait(false);
        StunMessage.TryParse(bytes, out var message); lock (_gate) AcceptLifetime(message);
    }
    // The ICE owner is the sole external control caller. Maintenance shares this semaphore.
    internal async Task CreateOwnedPermissionAsync(IPEndPoint peer, CancellationToken cancellationToken)
    {
        var safe = Admit(peer);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) RequireOpen();
            await PermissionCoreAsync(safe, cancellationToken).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }

    public async Task CreatePermissionAsync(IPEndPoint peer, CancellationToken cancellationToken = default)
    {
        var safe = Admit(peer); await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { await PermissionCoreAsync(safe, cancellationToken).ConfigureAwait(false); }
        finally { _operation.Release(); }
    }
    private async Task PermissionCoreAsync(IPEndPoint peer, CancellationToken ct)
    {
        var address = peer.Address.ToString();
        lock (_gate) if (!_permissions.ContainsKey(address) && _permissions.Count >= _options.MaximumPeers)
            throw new InvalidOperationException("TURN permission budget exhausted.");
        await ControlAsync(8, peer, null, null, ct).ConfigureAwait(false);
        lock (_gate) _permissions[address] = new(peer, After(300));
    }
    public async Task<ushort> BindChannelAsync(IPEndPoint peer, CancellationToken cancellationToken = default)
    {
        var safe = Admit(peer); await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { return await BindCoreAsync(safe, cancellationToken).ConfigureAwait(false); }
        finally { _operation.Release(); }
    }
    private async Task<ushort> BindCoreAsync(IPEndPoint peer, CancellationToken ct)
    {
        Binding binding;
        lock (_gate)
        {
            if (!_permissions.ContainsKey(peer.Address.ToString()) && _permissions.Count >= _options.MaximumPeers)
                throw new InvalidOperationException("TURN permission budget exhausted.");
            if (!_bindings.TryGetValue(peer.ToString(), out binding!))
            {
                if (_bindings.Count >= _options.MaximumPeers) throw new InvalidOperationException("TURN channel budget exhausted.");
                binding = new(peer, _nextChannel++); _bindings.Add(peer.ToString(), binding);
            }
            // Retain endpoint/channel even on cancellation: the server may already have bound it.
            // This avoids reuse with another endpoint while an old lease may still exist.
            binding.Pending = true;
        }
        try
        {
            await ControlAsync(9, peer, binding.Number, null, ct).ConfigureAwait(false);
            lock (_gate) { binding.Expires = After(600); _permissions[peer.Address.ToString()] = new(peer, After(300)); }
            return binding.Number;
        }
        finally { lock (_gate) binding.Pending = false; }
    }
    public async ValueTask SendDatagramAsync(IPEndPoint peer, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var safe = Admit(peer); byte[] packet;
        lock (_gate)
        {
            RequireOpen();
            if (data.Length > _options.MaximumDatagramSize) throw new ArgumentOutOfRangeException(nameof(data));
            if (!_permissions.TryGetValue(safe.Address.ToString(), out var permission) || Now >= permission.Expires)
                throw new InvalidOperationException("An active TURN permission is required before sending.");
            if (_bindings.TryGetValue(safe.ToString(), out var binding) && Now < binding.Expires)
            {
                packet = new byte[4 + data.Length]; BinaryPrimitives.WriteUInt16BigEndian(packet, binding.Number);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)data.Length); data.Span.CopyTo(packet.AsSpan(4));
            }
            else
            {
                var buffer = new byte[64 + data.Length]; var writer = new StunMessageWriter(buffer, 0x0016, RandomNumberGenerator.GetBytes(12));
                if (!writer.TryAddXorAddress(0x0012, safe) || !writer.TryAddAttribute(0x0013, data.Span) || !writer.TryComplete([], true, out var size))
                    throw new InvalidOperationException("TURN Send framing failed.");
                packet = buffer[..size];
            }
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _socket.SendToAsync(packet, SocketFlags.None, _server, cancel.Token).ConfigureAwait(false);
    }
    public async IAsyncEnumerable<TurnDatagram> ReceiveDatagramsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return packet;
    }
    private async Task<byte[]> ControlAsync(ushort method, IPEndPoint? peer, ushort? channel, TurnCredentials? credentials, CancellationToken ct, bool release = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var budget = _options.Transactions.Timeout;
        lock (_gate) if (_allocated && !release) budget = TimeSpan.FromSeconds(Math.Max(.001, Math.Min(budget.TotalSeconds, (_expires - Now) / (double)Stopwatch.Frequency)));
        deadline.CancelAfter(budget);
        try
        {
        for (var challenge = 0; challenge <= 2; challenge++)
        {
            var request = BuildRequest(method, peer, channel, release);
            var response = await ExchangeAsync(request, method, deadline.Token).ConfigureAwait(false);
            StunMessage.TryParse(response, out var message);
            if (message.Type == (method | 0x0100)) return response;
            if (!message.TryGetUniqueAttribute(0x0009, out var error) || error.Length < 4 || error[0] != 0 || error[1] != 0 || error[2] is < 3 or > 6 || error[3] > 99)
                throw new InvalidDataException("Malformed TURN error response.");
            var code = error[2] * 100 + error[3];
            if (code == 437) { lock (_gate) _allocated = false; if (release) return response; Stop(new IOException("TURN allocation no longer exists.")); }
            if (challenge == 2 || !(code == 401 && _key.Length == 0 && credentials != null || code == 438 && _key.Length != 0))
                throw new IOException($"TURN request failed with code {code}.");
            AcceptChallenge(message, credentials);
        }
        throw new IOException("TURN authentication challenge budget exhausted.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("TURN request deadline or allocation lifetime expired."); }
    }
    private byte[] BuildRequest(ushort method, IPEndPoint? peer, ushort? channel, bool release)
    {
        var bytes = new byte[4096]; var writer = new StunMessageWriter(bytes, method, RandomNumberGenerator.GetBytes(12));
        bool valid = true;
        if (method == 3) valid &= writer.TryAddUInt32(0x0019, 17u << 24);
        if (method is 3 or 4) { valid &= writer.TryAddUInt32(0x000D, release ? 0 : _options.RequestedLifetimeSeconds); valid &= writer.TryAddAttribute(0x8022, "tryAGI.WebRTC"u8); }
        if (peer != null) valid &= writer.TryAddXorAddress(0x0012, peer);
        if (channel != null) valid &= writer.TryAddUInt32(0x000C, (uint)channel << 16);
        if (_key.Length != 0)
        {
            valid &= writer.TryAddAttribute(_userhash.Length == 0 ? (ushort)0x0006 : (ushort)0x001E, _userhash.Length == 0 ? _username : _userhash);
            valid &= writer.TryAddAttribute(Realm, Encoding.ASCII.GetBytes(_realm!)); valid &= writer.TryAddAttribute(Nonce, Encoding.ASCII.GetBytes(_nonce!));
            if (_algorithms.Length != 0) { valid &= writer.TryAddAttribute(PasswordAlgorithms, _algorithms); valid &= writer.TryAddUInt32(PasswordAlgorithm, (uint)_algorithm << 16); }
        }
        if (!valid || !writer.TryComplete(_key, true, out var size, _modern)) throw new InvalidOperationException("TURN request framing failed.");
        return bytes[..size];
    }
    private void AcceptChallenge(StunMessage message, TurnCredentials? credentials)
    {
        if (!message.TryGetUniqueAttribute(Realm, out var realmBytes) || !message.TryGetUniqueAttribute(Nonce, out var nonceBytes) ||
            realmBytes.Length is < 1 or > 128 || nonceBytes.Length is < 1 or > 763 ||
            realmBytes.ContainsAnyInRange((byte)0, (byte)31) || realmBytes.ContainsAnyInRange((byte)127, byte.MaxValue) ||
            nonceBytes.ContainsAnyInRange((byte)0, (byte)31) || nonceBytes.ContainsAnyInRange((byte)127, byte.MaxValue))
            throw new InvalidDataException("Malformed TURN realm/nonce.");
        var realm = Encoding.ASCII.GetString(realmBytes); var nonce = Encoding.ASCII.GetString(nonceBytes);
        if (_realm != null && _realm != realm || _nonce == nonce) throw new InvalidDataException("TURN challenge changed realm or repeated nonce.");
        byte features = 0;
        if (nonce.StartsWith("obMatJos2", StringComparison.Ordinal))
        {
            Span<byte> bits = stackalloc byte[3];
            if (nonce.Length < 13 || !Convert.TryFromBase64Chars(nonce.AsSpan(9, 4), bits, out var n) || n != 3 ||
                (bits[0] & 0x3F) != 0 || bits[1] != 0 || bits[2] != 0) throw new InvalidDataException("Unsupported TURN nonce security features.");
            features = bits[0];
        }
        var hasAlgorithms = message.TryGetUniqueAttribute(PasswordAlgorithms, out var algorithms);
        if ((features & 0x80) != 0 && !hasAlgorithms) throw new InvalidDataException("TURN algorithm downgrade detected.");
        ushort algorithm = 1;
        if (hasAlgorithms)
        {
            if (algorithms.Length is < 4 or > 128) throw new InvalidDataException("Invalid TURN password algorithms.");
            algorithm = 0; var position = 0;
            while (position < algorithms.Length)
            {
                if (algorithms.Length - position < 4) throw new InvalidDataException("Truncated TURN password algorithm.");
                var id = BinaryPrimitives.ReadUInt16BigEndian(algorithms[position..]); var length = BinaryPrimitives.ReadUInt16BigEndian(algorithms[(position + 2)..]);
                var size = 4 + ((length + 3) & ~3);
                if (size > algorithms.Length - position) throw new InvalidDataException("Truncated TURN algorithm parameters.");
                if (id is 1 or 2 && length == 0 && (id == 2 || algorithm == 0)) algorithm = id;
                position += size;
            }
            if (algorithm == 0) throw new NotSupportedException("TURN server offers no supported password algorithm.");
        }
        if (algorithm == 1 && !_options.AllowLegacyAuthentication) throw new NotSupportedException("Legacy TURN authentication is disabled.");
        if (_key.Length != 0 && (_modern != hasAlgorithms || _algorithm != algorithm || (_userhash.Length != 0) != ((features & 0x40) != 0) || !_algorithms.AsSpan().SequenceEqual(algorithms)))
            throw new InvalidDataException("TURN challenge changed negotiated authentication.");
        if (_key.Length == 0)
        {
            _key = credentials!.Derive(realm, algorithm == 2); _username = Encoding.ASCII.GetBytes(credentials.Username);
            if ((features & 0x40) != 0) _userhash = SHA256.HashData(Encoding.ASCII.GetBytes(credentials.Username + ":" + realm));
        }
        _realm = realm; _nonce = nonce; _modern = hasAlgorithms; _algorithm = algorithm; _algorithms = algorithms.ToArray();
    }
    private async Task<byte[]> ExchangeAsync(byte[] packet, ushort method, CancellationToken ct)
    {
        var pending = new Pending(packet.AsSpan(8, 12).ToArray(), method, _key, _modern);
        lock (_gate) { if (_stopped) throw new ObjectDisposedException(nameof(TurnUdpAllocation)); _pending = pending; }
        try
        {
            for (var request = 0; request < _options.Transactions.MaximumRequests; request++)
            {
                if (pending.Done.Task.IsCompleted) return await pending.Done.Task.ConfigureAwait(false);
                await _socket.SendToAsync(packet, SocketFlags.None, _server, ct).ConfigureAwait(false);
                lock (_gate) { _sent++; if (request != 0) _retries++; }
                var multiplier = request == _options.Transactions.MaximumRequests - 1 ? _options.Transactions.FinalWaitMultiplier : 1 << request;
                try { return await pending.Done.Task.WaitAsync(TimeSpan.FromTicks(_options.Transactions.InitialRetransmissionTimeout.Ticks * multiplier), ct).ConfigureAwait(false); }
                catch (TimeoutException) when (request < _options.Transactions.MaximumRequests - 1) { }
            }
            throw new TimeoutException("TURN request exhausted its retransmission budget.");
        }
        finally { lock (_gate) _pending = null; }
    }
    private async Task ReadAsync()
    {
        var bytes = new byte[_options.MaximumDatagramSize + 4096];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await _socket.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(_local.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), _lifetime.Token).ConfigureAwait(false); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize)
                { lock (_gate) _rejected++; continue; }
                lock (_gate) HandlePacket(bytes.AsSpan(0, received.ReceivedBytes), (IPEndPoint)received.RemoteEndPoint);
            }
        }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) Stop(error); }
    }
    private void HandlePacket(ReadOnlySpan<byte> bytes, IPEndPoint source)
    {
        if (_stopped || !source.Equals(_server)) { _rejected++; return; }
        if (bytes.Length >= 4 && bytes[0] is >= 0x40 and <= 0x4F)
        {
            var channel = BinaryPrimitives.ReadUInt16BigEndian(bytes); var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
            var binding = _bindings.Values.FirstOrDefault(b => b.Number == channel && (b.Pending || Now < b.Expires));
            if (binding == null || length > _options.MaximumDatagramSize || bytes.Length < 4 + length || bytes.Length > 4 + length + 3)
            { _rejected++; return; }
            Deliver(binding.Peer, bytes.Slice(4, length), binding.Pending); return;
        }
        if (!StunMessage.TryParse(bytes, out var message) ||
            (Count(message, StunMessage.Fingerprint) != 0 || _options.Transactions.RequireFingerprint) && !message.VerifyFingerprint()) { _rejected++; return; }
        if (message.Type == 0x0017)
        {
            var attrs = message.GetAttributes();
            while (attrs.MoveNext()) if (attrs.Type < 0x8000 && attrs.Type is not (0x0012 or 0x0013)) { _rejected++; return; }
            if (!message.TryGetXorEndpoint(0x0012, out var peer) || !message.TryGetUniqueAttribute(0x0013, out var data)) { _rejected++; return; }
            Deliver(peer!, data, false); return;
        }
        var pending = _pending;
        if (bytes.Length > 4096 || pending == null || !message.TransactionId.SequenceEqual(pending.Id) ||
            message.Type != (pending.Method | 0x0100) && message.Type != (pending.Method | 0x0110)) { _rejected++; return; }
        var challenge = message.Type == (pending.Method | 0x0110) && message.TryGetUniqueAttribute(0x0009, out var error) &&
            error.Length >= 4 && error[0] == 0 && error[1] == 0 && (error[2] == 4 && error[3] == 38 || pending.Key.Length == 0 && error[2] == 4 && error[3] == 1);
        var hasIntegrity = Count(message, StunMessage.MessageIntegrity) + Count(message, StunMessage.MessageIntegritySha256) != 0;
        var authenticated = pending.Modern ? message.VerifyMessageIntegritySha256(pending.Key) : message.VerifyMessageIntegritySha1(pending.Key);
        if ((!challenge || hasIntegrity) && !authenticated) { _rejected++; return; }
        foreach (var type in new ushort[] { Realm, Nonce, PasswordAlgorithms, PasswordAlgorithm, 0x0009, 0x000D, 0x0016, 0x0020 })
            if (Count(message, type) > 1) { pending.Done.TrySetException(new InvalidDataException("Duplicate TURN response attribute.")); return; }
        var attributes = message.GetAttributes();
        while (attributes.MoveNext())
            if (attributes.Type < 0x8000 && attributes.Type is not (0x0006 or 0x0008 or 0x0009 or 0x000A or 0x000D or 0x0014 or 0x0015 or 0x0016 or 0x001C or 0x001D or 0x001E or 0x0020))
            { pending.Done.TrySetException(new InvalidDataException("Unknown required TURN response attribute.")); return; }
        pending.Done.TrySetResult(bytes.ToArray());
    }
    private void Deliver(IPEndPoint peer, ReadOnlySpan<byte> data, bool pendingChannel)
    {
        if (!_allocated || Now >= _expires || data.Length > _options.MaximumDatagramSize || peer.AddressFamily != _local.AddressFamily)
        { _rejected++; return; }
        try { peer = Admit(peer); } catch (ArgumentException) { _rejected++; return; }
        if (!pendingChannel && (!_permissions.TryGetValue(peer.Address.ToString(), out var permission) || Now >= permission.Expires)) { _rejected++; return; }
        _received++; _incoming.Writer.TryWrite(new(peer, data.ToArray()));
    }
    private static int Count(StunMessage message, ushort type)
    { var attributes = message.GetAttributes(); var count = 0; while (attributes.MoveNext()) if (attributes.Type == type) count++; return count; }
    private async Task MaintainAsync()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_maintenanceLifetime.Token, _lifetime.Token);
        try
        {
            while (true)
            {
                double remaining; lock (_gate) remaining = (_expires - Now) / (double)Stopwatch.Frequency;
                await _maintenanceWake.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(remaining / 4, .01, 5)), cancel.Token).ConfigureAwait(false);
                await _operation.WaitAsync(cancel.Token).ConfigureAwait(false);
                try
                {
                    Permission[] permissions; Binding[] bindings; bool refresh;
                    lock (_gate) { if (_disposing) return; RequireOpen(); refresh = Now >= _refreshAt; permissions = _permissions.Values.Where(p => p.Expires - Now < 60 * Stopwatch.Frequency).ToArray(); bindings = _bindings.Values.Where(b => b.Expires > 0 && b.Expires - Now < 60 * Stopwatch.Frequency).ToArray(); }
                    if (refresh) await RefreshCoreAsync(cancel.Token).ConfigureAwait(false);
                    foreach (var permission in permissions) await PermissionCoreAsync(permission.Peer, cancel.Token).ConfigureAwait(false);
                    foreach (var binding in bindings) await BindCoreAsync(binding.Peer, cancel.Token).ConfigureAwait(false);
                }
                finally { _operation.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { Stop(error); }
    }
    private void Stop(Exception? error)
    {
        lock (_gate)
        {
            if (_stopped) return; _stopped = true; _allocated = false;
            _pending?.Done.TrySetException(error ?? new ObjectDisposedException(nameof(TurnUdpAllocation)));
            _incoming.Writer.TryComplete(error); _completion.TrySetResult(error);
        }
        _lifetime.Cancel(); _socket.Dispose();
    }
    public ValueTask DisposeAsync()
    { lock (_gate) return new(_dispose ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        _disposing = true; _maintenanceLifetime.Cancel();
        if (_maintenance != null) await _maintenance.ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _operation.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (_allocated && !_stopped)
                {
                    var response = await ControlAsync(4, null, null, null, deadline.Token, release: true).ConfigureAwait(false);
                    StunMessage.TryParse(response, out var message);
                    if (message.Type == 0x0114 || message.TryGetUniqueAttribute(0x000D, out var value) && value.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(value) == 0) _released = true;
                }
            }
            finally { _operation.Release(); }
        }
        catch (Exception) when (deadline.IsCancellationRequested || _stopped) { }
        catch (TimeoutException) { }
        catch (SocketException) { }
        catch (IOException) { } // Best effort remote deletion; server expiry remains the fallback.
        finally { Stop(null); }
        await _reader.ConfigureAwait(false);
        // Stop wakes any caller-owned control operation; wait before clearing its shared key.
        await _operation.WaitAsync().ConfigureAwait(false);
        try { lock (_gate) { CryptographicOperations.ZeroMemory(_key); _key = []; _username = []; _userhash = []; _realm = null; _nonce = null; _permissions.Clear(); _bindings.Clear(); } }
        finally { _operation.Release(); }
    }
    private sealed record Permission(IPEndPoint Peer, long Expires);
    private sealed class Binding(IPEndPoint peer, ushort number)
    { public IPEndPoint Peer { get; } = peer; public ushort Number { get; } = number; public long Expires; public bool Pending; }
    private sealed record Pending(byte[] Id, ushort Method, byte[] Key, bool Modern)
    { public TaskCompletionSource<byte[]> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
}
