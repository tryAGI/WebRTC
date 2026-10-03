using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public sealed record IceUdpTransportOptions
{
    public int MaximumCandidatePairs { get; init; } = 64;
    public int MaximumDataDatagramSize { get; init; } = 1200;
    public int ReceiveQueueCapacity { get; init; } = 128;
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan InitialRetransmissionTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ConsentInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConsentTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaximumCandidatePairs is < 1 or > 64 || MaximumDataDatagramSize is < 64 or > 65507 ||
            ReceiveQueueCapacity is < 1 or > 1024 ||
            CheckInterval < TimeSpan.FromMilliseconds(5) || CheckInterval > TimeSpan.FromMilliseconds(100) ||
            InitialRetransmissionTimeout < TimeSpan.FromMilliseconds(100) || InitialRetransmissionTimeout > TimeSpan.FromSeconds(2) ||
            ConnectionTimeout < TimeSpan.FromMilliseconds(100) || ConnectionTimeout > TimeSpan.FromMinutes(1) ||
            ConsentInterval < TimeSpan.FromMilliseconds(100) || ConsentInterval > TimeSpan.FromSeconds(5) ||
            ConsentTimeout < 3 * ConsentInterval || ConsentTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(IceUdpTransportOptions));
    }
}

public sealed record IceUdpTransportDiagnostics(
    IceRole Role, int CandidatePairs, IPEndPoint? SelectedRemoteEndPoint,
    TimeSpan? ConnectionTime, TimeSpan? LastCheckRoundTripTime,
    long SentChecks, long Retransmissions, long ValidatedRequests, long RoleConflicts, long DroppedDatagrams,
    int BufferedEarlyChecks);

/// <summary>
/// Single-component UDP ICE connectivity for resolved host/reflexive candidates.
/// Datagrams are NOT media-authenticated: DTLS/SRTP must be layered above this transport.
/// Gathering, TURN routing, mDNS and ICE restart are not implemented here yet.
/// </summary>
public sealed class IceUdpTransport : IAsyncDisposable
{
    private const ushort Username = 0x0006, Priority = 0x0024, UseCandidate = 0x0025;
    private const ushort Controlled = 0x8029, Controlling = 0x802A, ErrorCode = 0x0009;
    private const ushort BindingSuccess = 0x0101, BindingError = 0x0111;
    private readonly object _gate = new();
    private readonly Socket _socket;
    private readonly IceUdpTransportOptions _options;
    private readonly IceCredentials _localCredentials;
    private readonly byte[] _localKey;
    private readonly byte[] _localUsernamePrefix;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _datagrams;
    private readonly List<Pair> _pairs = [];
    private readonly Dictionary<string, Transaction> _transactions = [];
    private readonly List<EarlyCheck> _earlyChecks = [];
    private readonly Task _receiver, _checker;
    private readonly ulong _tieBreaker;
    private byte[]? _remoteKey, _outgoingUsername, _incomingUsername;
    private IceRole _role;
    private Pair? _selected;
    private long _startedAt, _selectedAt, _lastConsentAt, _nextConsentAt, _lastCheckSentAt;
    private TimeSpan? _lastRoundTrip;
    private bool _started, _stopped;
    private int _disposed;
    private long _sentChecks, _retransmissions, _validatedRequests, _roleConflicts, _droppedDatagrams;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public IceCredentials LocalCredentials => _localCredentials;
    public bool IsConnected
    {
        get { lock (_gate) return !_stopped && _selected is not null && Elapsed(_lastConsentAt) < _options.ConsentTimeout; }
    }
    /// <summary>Completes with the shutdown reason. A successful ConnectAsync is not secure-media readiness.</summary>
    public Task<Exception> Completion => _completion.Task;

    public IceUdpTransport(IPEndPoint localEndPoint, IceCredentials? credentials = null, IceUdpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        _options = options ?? new();
        _options.Validate();
        _localCredentials = credentials ?? IceCredentials.Generate();
        _localKey = Encoding.ASCII.GetBytes(_localCredentials.Password);
        _localUsernamePrefix = Encoding.ASCII.GetBytes(_localCredentials.UsernameFragment + ":");
        _tieBreaker = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        _socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try { _socket.Bind(localEndPoint); }
        catch { _socket.Dispose(); throw; }
        _datagrams = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_options.ReceiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
            AllowSynchronousContinuations = false,
        }, _ => Interlocked.Increment(ref _droppedDatagrams));
        _receiver = Task.Run(ReceiveLoopAsync);
        _checker = Task.Run(CheckLoopAsync);
    }

    /// <summary>Starts one credential generation. Trickle candidates may be added while this task awaits nomination.</summary>
    public async Task ConnectAsync(IceCredentials remoteCredentials, IceRole role,
        IEnumerable<IceCandidate> remoteCandidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteCredentials);
        ArgumentNullException.ThrowIfNull(remoteCandidates);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        // Materialize at most one more than the limit; unbounded caller enumerables must not consume unbounded memory.
        var candidates = remoteCandidates.Take(_options.MaximumCandidatePairs + 1).ToArray();
        if (candidates.Length > _options.MaximumCandidatePairs) throw new ArgumentException("Too many ICE candidate pairs.", nameof(remoteCandidates));
        foreach (var candidate in candidates) ValidateCandidate(candidate);
        var earlyResponses = new List<(byte[] Packet, IPEndPoint Destination)>();
        lock (_gate)
        {
            ThrowIfStopped();
            if (_started) throw new InvalidOperationException("A transport supports one ICE credential generation.");
            _started = true;
            _startedAt = Stopwatch.GetTimestamp();
            _role = role;
            _remoteKey = Encoding.ASCII.GetBytes(remoteCredentials.Password);
            _outgoingUsername = Encoding.ASCII.GetBytes($"{remoteCredentials.UsernameFragment}:{_localCredentials.UsernameFragment}");
            _incomingUsername = Encoding.ASCII.GetBytes($"{_localCredentials.UsernameFragment}:{remoteCredentials.UsernameFragment}");
            foreach (var candidate in candidates) AddCandidateCore(candidate);
            foreach (var check in _earlyChecks)
            {
                if (Elapsed(check.ReceivedAt) > TimeSpan.FromSeconds(2)) continue;
                // Full remote username/role checks occur only after signaling supplied credentials.
                if (HandleStun(check.Packet, check.Source) is { } response) earlyResponses.Add((response, check.Source));
            }
            _earlyChecks.Clear();
        }
        try
        {
            foreach (var response in earlyResponses)
            {
                try { await _socket.SendToAsync(response.Packet, SocketFlags.None, response.Destination, cancellationToken).ConfigureAwait(false); }
                catch (SocketException exception) when (IsRemoteNetworkError(exception)) { }
            }
            WakeChecker();
            await _connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop(new OperationCanceledException("ICE connection was cancelled.", cancellationToken));
            throw;
        }
        catch (Exception exception) { Stop(exception); throw; }
    }

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        ValidateCandidate(candidate);
        lock (_gate)
        {
            ThrowIfStopped();
            if (!_started) throw new InvalidOperationException("Start the credential generation before trickling candidates.");
            AddCandidateCore(candidate);
        }
        WakeChecker();
    }

    /// <summary>Requires a nominated pair and fresh outbound consent. The upper layer supplies DTLS/SRTP ciphertext.</summary>
    public async ValueTask SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        if (datagram.Length == 0 || datagram.Length > _options.MaximumDataDatagramSize)
            throw new ArgumentOutOfRangeException(nameof(datagram));
        IPEndPoint destination;
        lock (_gate)
        {
            ThrowIfStopped();
            if (_selected is null || Elapsed(_lastConsentAt) >= _options.ConsentTimeout)
                throw new InvalidOperationException("A nominated ICE pair with fresh consent is required.");
            destination = _selected.Candidate.TransportEndPoint;
        }
        await _socket.SendToAsync(datagram, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<byte[]> ReceiveDatagramsAsync(CancellationToken cancellationToken = default) =>
        _datagrams.Reader.ReadAllAsync(cancellationToken);

    public IceUdpTransportDiagnostics GetDiagnostics()
    {
        lock (_gate)
            return new(_role, _pairs.Count, _selected?.Candidate.EndPoint,
                _selected is null ? null : Stopwatch.GetElapsedTime(_startedAt, _selectedAt), _lastRoundTrip,
                _sentChecks, _retransmissions, _validatedRequests, _roleConflicts, Interlocked.Read(ref _droppedDatagrams), _earlyChecks.Count);
    }

    private void ValidateCandidate(IceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.TransportEndPoint.AddressFamily != _socket.AddressFamily)
            throw new ArgumentException("Candidate and local socket address families must match.", nameof(candidate));
        if (candidate.Type == IceCandidateType.Relay)
            throw new NotSupportedException("Relayed candidates require TURN allocation/routing, not a direct UDP socket.");
    }

    private Pair AddCandidateCore(IceCandidate candidate)
    {
        var existing = _pairs.Find(p => p.Candidate.TransportEndPoint.Equals(candidate.TransportEndPoint));
        if (existing is not null) return existing;
        if (_pairs.Count >= _options.MaximumCandidatePairs) throw new InvalidOperationException("ICE candidate pair limit exceeded.");
        var pair = new Pair(candidate);
        _pairs.Add(pair);
        return pair;
    }

    private async Task CheckLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                (byte[] Packet, IPEndPoint Destination)? send;
                lock (_gate) send = GetNextCheck();
                if (send is { } next)
                {
                    try { await _socket.SendToAsync(next.Packet, SocketFlags.None, next.Destination, _lifetime.Token).ConfigureAwait(false); }
                    catch (SocketException exception) when (IsRemoteNetworkError(exception) && !_lifetime.IsCancellationRequested)
                    {
                        // A single unreachable candidate or delayed ICMP must not abort the checklist.
                        // Keep bounded retry/consent deadlines as the authoritative liveness test.
                    }
                }
                await _wake.WaitAsync(_options.CheckInterval, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Stop(exception); }
    }

    private (byte[] Packet, IPEndPoint Destination)? GetNextCheck()
    {
        _earlyChecks.RemoveAll(c => Elapsed(c.ReceivedAt) > TimeSpan.FromSeconds(2));
        if (!_started || _stopped) return null;
        var now = Stopwatch.GetTimestamp();
        if (_selected is null && Elapsed(_startedAt) >= _options.ConnectionTimeout)
            throw new TimeoutException("ICE nomination did not complete before its deadline.");
        if (_selected is not null && Elapsed(_lastConsentAt) >= _options.ConsentTimeout)
            throw new IOException("ICE consent expired; outbound traffic is stopped.");
        if (_lastCheckSentAt != 0 && Elapsed(_lastCheckSentAt) < _options.CheckInterval) return null;
        Transaction? transaction = null;
        if (_selected is not null)
        {
            if (now < _nextConsentAt) return null;
            // Consent requests are fresh transactions, never retransmitted (RFC 7675).
            foreach (var id in _transactions.Where(p => p.Value.Consent).Select(p => p.Key).ToArray()) _transactions.Remove(id);
            transaction = CreateTransaction(_selected, useCandidate: false, consent: true);
            _nextConsentAt = Add(now, _options.ConsentInterval * (RandomNumberGenerator.GetInt32(800, 1201) / 1000.0));
        }
        else
        {
            foreach (var pair in _pairs.OrderByDescending(PairPriority))
            {
                if (pair.Failed) continue;
                if (pair.Active is { } active)
                {
                    if (now < active.NextSendAt) continue;
                    if (active.Attempts >= 7)
                    {
                        _transactions.Remove(active.Id);
                        pair.Active = null;
                        pair.Failed = true;
                        continue;
                    }
                    transaction = active;
                    _retransmissions++;
                    break;
                }
                if (pair.Validated && _role == IceRole.Controlled) continue;
                transaction = CreateTransaction(pair, useCandidate: pair.Validated && _role == IceRole.Controlling, consent: false);
                pair.Active = transaction;
                break;
            }
        }
        if (transaction is null) return null;
        transaction.Attempts++;
        transaction.LastSendAt = now;
        transaction.NextSendAt = Add(now, _options.InitialRetransmissionTimeout * (1 << Math.Min(transaction.Attempts - 1, 3)));
        _lastCheckSentAt = now;
        _sentChecks++;
        return (transaction.Packet, transaction.Pair.Candidate.TransportEndPoint);
    }

    private ulong PairPriority(Pair pair)
    {
        uint local = 2130706431, remote = pair.Candidate.Priority;
        var g = _role == IceRole.Controlling ? local : remote;
        var d = _role == IceRole.Controlling ? remote : local;
        return ((ulong)Math.Min(g, d) << 32) + 2UL * Math.Max(g, d) + (g > d ? 1UL : 0UL);
    }

    private Transaction CreateTransaction(Pair pair, bool useCandidate, bool consent)
    {
        var id = RandomNumberGenerator.GetBytes(12);
        var buffer = new byte[1024];
        var writer = new StunMessageWriter(buffer, StunMessage.BindingRequest, id);
        var ok = writer.TryAddAttribute(Username, _outgoingUsername!);
        if (!consent)
        {
            ok &= writer.TryAddUInt32(Priority, 1862270975); // Peer-reflexive priority for component 1.
            ok &= writer.TryAddUInt64(_role == IceRole.Controlling ? Controlling : Controlled, _tieBreaker);
            if (useCandidate) ok &= writer.TryAddAttribute(UseCandidate, []);
        }
        if (!ok || !writer.TryComplete(_remoteKey!, fingerprint: true, out var length))
            throw new InvalidOperationException("ICE request exceeded its bounded STUN storage.");
        var transaction = new Transaction(Convert.ToHexString(id), buffer[..length], pair, _role, useCandidate, consent);
        _transactions.Add(transaction.Id, transaction);
        return transaction;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];
        EndPoint sourceTemplate = _socket.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, sourceTemplate, _lifetime.Token).ConfigureAwait(false); }
                catch (SocketException exception) when (IsRemoteNetworkError(exception) && !_lifetime.IsCancellationRequested) { continue; }
                var source = (IPEndPoint)received.RemoteEndPoint;
                var length = received.ReceivedBytes;
                if (length == 0) continue;
                if (buffer[0] <= 3)
                {
                    byte[]? response;
                    lock (_gate) response = HandleStun(buffer.AsSpan(0, length), source);
                    if (response is not null)
                    {
                        try { await _socket.SendToAsync(response, SocketFlags.None, source, _lifetime.Token).ConfigureAwait(false); }
                        catch (SocketException exception) when (IsRemoteNetworkError(exception) && !_lifetime.IsCancellationRequested) { }
                    }
                    WakeChecker();
                }
                else
                {
                    lock (_gate)
                    {
                        if (!_stopped && _selected is not null && source.Equals(_selected.Candidate.TransportEndPoint) &&
                            length <= _options.MaximumDataDatagramSize && Elapsed(_lastConsentAt) < _options.ConsentTimeout)
                            _datagrams.Writer.TryWrite(buffer[..length]);
                        else Interlocked.Increment(ref _droppedDatagrams);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Stop(exception); }
    }

    private byte[]? HandleStun(ReadOnlySpan<byte> packet, IPEndPoint source)
    {
        if (_stopped || packet.Length > 2048 || !StunMessage.TryParse(packet, out var message) || !message.VerifyFingerprint())
            return null;
        if (!_started)
        {
            BufferEarlyCheck(message, source);
            return null;
        }
        if (message.Type != StunMessage.BindingRequest)
        {
            HandleResponse(message, source);
            return null;
        }
        if (!message.TryGetUniqueAttribute(Username, out var username) || !username.SequenceEqual(_incomingUsername) ||
            !message.VerifyMessageIntegritySha1(_localKey)) return null;

        var attributes = message.GetAttributes();
        while (attributes.MoveNext())
        {
            if (attributes.Type < 0x8000 && attributes.Type is not (Username or Priority or UseCandidate or StunMessage.MessageIntegrity))
                return null; // Unknown required attributes are unsupported; no connectivity is accepted.
        }
        var hasControlling = message.TryGetUniqueAttribute(Controlling, out var controlling);
        var hasControlled = message.TryGetUniqueAttribute(Controlled, out var controlled);
        var hasPriority = message.TryGetUniqueAttribute(Priority, out var priority);
        var hasUseCandidate = message.TryGetUniqueAttribute(UseCandidate, out var useCandidate);
        // Detect duplicates even where a failed unique lookup otherwise resembles absence.
        if (Count(message, Controlling) > 1 || Count(message, Controlled) > 1 || Count(message, Priority) > 1 || Count(message, UseCandidate) > 1)
            return null;
        if (!hasPriority && !hasControlling && !hasControlled && !hasUseCandidate)
        {
            // Pure consent is valid only for the already nominated source.
            if (_selected is null || !source.Equals(_selected.Candidate.TransportEndPoint)) return null;
            _validatedRequests++;
            return BuildResponse(message, source, roleConflict: false);
        }
        if (hasControlling == hasControlled || (hasControlling ? controlling.Length : controlled.Length) != 8 ||
            !hasPriority || priority.Length != 4 || (hasUseCandidate && (useCandidate.Length != 0 || !hasControlling))) return null;
        var remotePriority = BinaryPrimitives.ReadUInt32BigEndian(priority);
        if (remotePriority == 0 || remotePriority > int.MaxValue) return null;
        var remoteTie = BinaryPrimitives.ReadUInt64BigEndian(hasControlling ? controlling : controlled);
        if ((hasControlling && _role == IceRole.Controlling) || (hasControlled && _role == IceRole.Controlled))
        {
            _roleConflicts++;
            if ((_role == IceRole.Controlling && _tieBreaker >= remoteTie) || (_role == IceRole.Controlled && _tieBreaker < remoteTie))
                return BuildResponse(message, source, roleConflict: true);
            ChangeRole(_role == IceRole.Controlling ? IceRole.Controlled : IceRole.Controlling);
        }
        Pair pair;
        try { pair = AddCandidateCore(new IceCandidate(source, remotePriority, IceCandidateType.PeerReflexive)); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        _validatedRequests++;
        pair.Failed = false;
        if (hasUseCandidate && _role == IceRole.Controlled)
        {
            pair.RemoteNominated = true;
            if (pair.Validated) SelectPair(pair);
        }
        return BuildResponse(message, source, roleConflict: false);
    }

    private void HandleResponse(StunMessage message, IPEndPoint source)
    {
        if (message.Type is not (BindingSuccess or BindingError) ||
            !_transactions.TryGetValue(Convert.ToHexString(message.TransactionId), out var transaction) ||
            !source.Equals(transaction.Pair.Candidate.TransportEndPoint) || !message.VerifyMessageIntegritySha1(_remoteKey!)) return;
        if (message.Type == BindingError)
        {
            if (message.TryGetUniqueAttribute(ErrorCode, out var code) && code.Length >= 4 && code[2] == 4 && code[3] == 87)
            {
                _roleConflicts++;
                if (transaction.Role == _role) ChangeRole(_role == IceRole.Controlling ? IceRole.Controlled : IceRole.Controlling);
            }
            return;
        }
        if (!message.TryGetXorMappedEndpoint(out _)) return;
        _transactions.Remove(transaction.Id);
        transaction.Pair.Active = null;
        _lastRoundTrip = Elapsed(transaction.LastSendAt);
        if (transaction.Consent)
        {
            if (Elapsed(_lastConsentAt) < _options.ConsentTimeout) _lastConsentAt = Stopwatch.GetTimestamp();
            return;
        }
        transaction.Pair.Validated = true;
        if ((_role == IceRole.Controlling && transaction.UseCandidate && transaction.Role == _role) ||
            (_role == IceRole.Controlled && transaction.Pair.RemoteNominated)) SelectPair(transaction.Pair);
    }

    private void BufferEarlyCheck(StunMessage message, IPEndPoint source)
    {
        if (message.Type != StunMessage.BindingRequest || !message.TryGetUniqueAttribute(Username, out var username) ||
            !username.StartsWith(_localUsernamePrefix) || !message.VerifyMessageIntegritySha1(_localKey)) return;
        var remoteFragment = username[_localUsernamePrefix.Length..];
        if (remoteFragment.Length is < 4 or > 256) return;
        foreach (var c in remoteFragment)
            if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/')) return;
        var id = Convert.ToHexString(message.TransactionId);
        _earlyChecks.RemoveAll(c => Elapsed(c.ReceivedAt) > TimeSpan.FromSeconds(2));
        if (_earlyChecks.Any(c => c.Id == id && c.Source.Equals(source))) return;
        if (_earlyChecks.Count >= 16) _earlyChecks.RemoveAt(0);
        _earlyChecks.Add(new(id, message.Data.ToArray(), source, Stopwatch.GetTimestamp()));
    }

    private static int Count(StunMessage message, ushort type)
    {
        var count = 0;
        var attributes = message.GetAttributes();
        while (attributes.MoveNext()) if (attributes.Type == type) count++;
        return count;
    }

    private byte[] BuildResponse(StunMessage request, IPEndPoint source, bool roleConflict)
    {
        var buffer = new byte[128];
        var writer = new StunMessageWriter(buffer, roleConflict ? BindingError : BindingSuccess, request.TransactionId);
        var ok = roleConflict ? writer.TryAddAttribute(ErrorCode, [0, 0, 4, 87]) : writer.TryAddXorMappedAddress(source);
        if (!ok || !writer.TryComplete(_localKey, fingerprint: true, out var written))
            throw new InvalidOperationException("ICE response exceeded its bounded storage.");
        return buffer[..written];
    }

    private void ChangeRole(IceRole role)
    {
        _role = role;
        _transactions.Clear();
        foreach (var pair in _pairs)
        {
            pair.Active = null;
            pair.Failed = false;
            pair.RemoteNominated = false;
        }
    }

    private void SelectPair(Pair pair)
    {
        if (_selected is not null) return;
        _selected = pair;
        _selectedAt = _lastConsentAt = Stopwatch.GetTimestamp();
        _nextConsentAt = Add(_selectedAt, _options.ConsentInterval);
        _transactions.Clear();
        foreach (var candidate in _pairs) candidate.Active = null;
        _connected.TrySetResult();
    }

    private void WakeChecker()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Stop(Exception reason)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _earlyChecks.Clear();
            if (reason is OperationCanceledException) _connected.TrySetCanceled();
            else _connected.TrySetException(reason);
            _completion.TrySetResult(reason);
            _datagrams.Writer.TryComplete(reason);
        }
        _lifetime.Cancel();
        _socket.Dispose();
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_stopped) throw new IOException("ICE transport has stopped.");
    }

    private static TimeSpan Elapsed(long timestamp) => Stopwatch.GetElapsedTime(timestamp);
    private static bool IsRemoteNetworkError(SocketException exception) => exception.SocketErrorCode is
        SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.NetworkUnreachable or
        SocketError.HostUnreachable or SocketError.AddressNotAvailable or SocketError.NetworkDown;
    private static long Add(long timestamp, TimeSpan duration) => timestamp + (long)(duration.TotalSeconds * Stopwatch.Frequency);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop(new OperationCanceledException("ICE transport disposed."));
        await Task.WhenAll(_receiver, _checker).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(_localKey);
        if (_remoteKey is not null) CryptographicOperations.ZeroMemory(_remoteKey);
        _wake.Dispose();
        _lifetime.Dispose();
    }

    private sealed class Pair(IceCandidate candidate)
    {
        public IceCandidate Candidate { get; } = candidate;
        public bool Validated, RemoteNominated, Failed;
        public Transaction? Active;
    }

    private sealed record EarlyCheck(string Id, byte[] Packet, IPEndPoint Source, long ReceivedAt);

    private sealed class Transaction(string id, byte[] packet, Pair pair, IceRole role, bool useCandidate, bool consent)
    {
        public string Id { get; } = id;
        public byte[] Packet { get; } = packet;
        public Pair Pair { get; } = pair;
        public IceRole Role { get; } = role;
        public bool UseCandidate { get; } = useCandidate;
        public bool Consent { get; } = consent;
        public int Attempts;
        public long NextSendAt, LastSendAt;
    }
}
