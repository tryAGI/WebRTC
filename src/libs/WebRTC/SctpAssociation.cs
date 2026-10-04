using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public enum SctpRole { Initiator, Responder }
public sealed record SctpOptions
{
    public bool EnablePartialReliability { get; init; } = true;
    public bool EnableStreamReset { get; init; } = true;
    public int MaximumQueuedResetEvents { get; init; } = 128;
    public ushort LocalPort { get; init; } = 5000;
    public ushort RemotePort { get; init; } = 5000;
    public ushort Streams { get; init; } = 128;
    public int MaximumPacketSize { get; init; } = 1152;
    public int MaximumMessageSize { get; init; } = 256 * 1024;
    public int ReceiveBufferBytes { get; init; } = 1024 * 1024;
    public int SendBufferBytes { get; init; } = 2 * 1024 * 1024;
    public int MaximumQueuedMessages { get; init; } = 128;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan InitialRetransmissionTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public int MaximumRetransmissions { get; init; } = 12;
    /// <summary>Optional explicit TSN for deterministic interoperability tests; defaults to cryptographic randomness.</summary>
    public uint? InitialTransmissionSequenceNumber { get; init; }
}
public abstract record SctpReceiveEvent;
public sealed record SctpMessage(ushort StreamId, uint PayloadProtocolIdentifier, bool Unordered, byte[] Data) : SctpReceiveEvent;
public sealed record SctpDiagnostics(long Retransmissions, long RejectedPackets, int BufferedSendBytes,
    int BufferedReceiveBytes, int OutstandingChunks, int CongestionWindowBytes, uint PeerAdvertisedWindowBytes, TimeSpan RetransmissionTimeout,
    long AbandonedMessages = 0);

/// <summary>One bounded, single-path SCTP/PR-SCTP association over authenticated DTLS datagrams.</summary>
public sealed partial class SctpAssociation : IAsyncDisposable
{
    private const int MaximumChunks = 4096;
    private readonly DtlsSrtpTransport _transport;
    private readonly SctpOptions _options;
    private readonly SctpRole _role;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TaskCompletionSource _connected = NewSignal();
    private readonly TaskCompletionSource _shutdown = NewSignal();
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<SctpReceiveEvent> _messages;
    private readonly Dictionary<uint, Outbound> _outbound = [];
    private readonly Dictionary<uint, Fragment> _fragments = [];
    private readonly HashSet<uint> _gaps = [];
    private readonly Dictionary<ushort, ushort> _sendSequences = [], _receiveSequences = [];
    private readonly Dictionary<ushort, Dictionary<ushort, SctpMessage>> _ordered = [];
    private readonly Queue<byte[]> _controls = [];
    private readonly Queue<SctpMessage> _unorderedPending = [];
    private readonly byte[] _cookieKey = RandomNumberGenerator.GetBytes(32);
    private readonly uint _localTag = RandomNonzero(), _initialTsn;
    private TaskCompletionSource _space = NewSignal(), _drained = NewSignal();
    private Task? _pump;
    private byte[]? _cookie, _peerCookie, _initAck, _flight;
    private uint _remoteTag, _remoteInitialTsn, _nextTsn, _acknowledgedTsn, _receiveTsn, _highestSentTsn, _lastGapTop;
    private uint _peerWindow;
    private ushort _outgoingStreams, _incomingStreams;
    private long _startedAt, _flightAt, _lastProbeAt;
    private readonly int _maximumChunkSize;
    private uint _fastRecoveryExit, _timeoutRecoveryTsn;
    private bool _fastRecovery, _timeoutRecovery;
    private int _sendBytes, _receiveBytes, _retainedMessages, _deliveredMessages, _started, _disposed, _receiving, _cwnd, _threshold, _partialAcked, _flightRetries;
    private double _smoothedRtt, _rttVariance;
    private TimeSpan _rto, _flightRto;
    private long _retransmissions, _rejected;
    private bool _receiveEnded;
    private Exception? _receiveFailure;
    private bool _hasRemoteInit, _ready, _closing, _peerShutdown, _shutdownSent, _shutdownAckSent, _finishAfterFlush, _sackNeeded, _peerShutdownComplete;

    internal EstablishmentJournal? Establishment { get; init; }
    public SctpRole Role => _role;
    public int MaximumMessageSize => _options.MaximumMessageSize;
    public DtlsRole DtlsRole => _transport.Role;
    public bool IsConnected { get { lock (_gate) return _ready && !_completion.Task.IsCompleted; } }
    public ushort OutgoingStreams { get { lock (_gate) return _outgoingStreams; } }
    public ushort IncomingStreams { get { lock (_gate) return _incomingStreams; } }
    public Task<Exception?> Completion => _completion.Task;

    public SctpAssociation(DtlsSrtpTransport transport, SctpRole role, SctpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        _options = options ?? new();
        if (_options.LocalPort == 0 || _options.RemotePort == 0 || _options.Streams is < 1 or > 128 ||
            _options.MaximumPacketSize < 128 || _options.MaximumPacketSize > transport.MaximumApplicationDatagramSize ||
            _options.MaximumMessageSize is < 1 or > 1024 * 1024 ||
            _options.ReceiveBufferBytes < Math.Max(1500, _options.MaximumMessageSize) || _options.ReceiveBufferBytes > 8 * 1024 * 1024 ||
            _options.SendBufferBytes < _options.MaximumMessageSize || _options.SendBufferBytes > 8 * 1024 * 1024 ||
            _options.MaximumQueuedMessages is < 1 or > 1024 || _options.MaximumQueuedResetEvents is < 2 or > MaximumResetEvents || _options.MaximumRetransmissions is < 1 or > 32 ||
            _options.HandshakeTimeout < TimeSpan.FromMilliseconds(100) || _options.HandshakeTimeout > TimeSpan.FromMinutes(1) ||
            _options.InitialRetransmissionTimeout < TimeSpan.FromMilliseconds(100) || _options.InitialRetransmissionTimeout > TimeSpan.FromSeconds(3))
            throw new ArgumentOutOfRangeException(nameof(options));
        _transport = transport; _role = role;
        _initialTsn = _options.InitialTransmissionSequenceNumber ?? RandomNonzero();
        _nextResetSequence = _initialTsn;
        _nextTsn = _initialTsn; _acknowledgedTsn = _highestSentTsn = unchecked(_initialTsn - 1);
        _lastGapTop = _acknowledgedTsn;
        _rto = _flightRto = _options.InitialRetransmissionTimeout;
        _maximumChunkSize = _options.MaximumPacketSize - 12;
        _cwnd = Math.Min(4 * _maximumChunkSize, Math.Max(2 * _maximumChunkSize, 4344));
        _threshold = _options.ReceiveBufferBytes;
        _messages = Channel.CreateBounded<SctpReceiveEvent>(new BoundedChannelOptions(_options.MaximumQueuedMessages + _options.MaximumQueuedResetEvents)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        _drained.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static uint RandomNonzero()
    { uint value; do { value = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); } while (value == 0); return value; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_transport.IsConnected) throw new InvalidOperationException("SCTP requires authenticated DTLS.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("One association per instance.");
        _startedAt = Stopwatch.GetTimestamp();
        Establishment?.Begin(EstablishmentPhase.Sctp, _role == SctpRole.Initiator ? HandshakeStep.SctpInitAck : HandshakeStep.SctpInit);
        _pump = RunAsync();
        try { await _connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _lifetime.Cancel(); throw; }
    }

    public SctpDiagnostics GetDiagnostics()
    {
        lock (_gate) return new(Interlocked.Read(ref _retransmissions), Interlocked.Read(ref _rejected), _sendBytes,
            _receiveBytes, _outbound.Count, _cwnd, _peerWindow, _rto, _abandonedMessages);
    }

    /// <summary>Queues one complete message with optional partial reliability. Timed lifetime starts at this call, including admission. Does not wait for acknowledgment.</summary>
    public async ValueTask SendMessageAsync(ushort streamId, uint payloadProtocolIdentifier, ReadOnlyMemory<byte> data,
        bool unordered = false, CancellationToken cancellationToken = default, SctpReliability? reliability = null)
    {
        await SendMessageCoreAsync(streamId, payloadProtocolIdentifier, data, unordered, cancellationToken, reliability, null).ConfigureAwait(false);
    }
    internal async ValueTask SendMessageCoreAsync(ushort streamId, uint payloadProtocolIdentifier, ReadOnlyMemory<byte> data,
        bool unordered, CancellationToken cancellationToken, SctpReliability? reliability, Func<bool>? admissionGuard)
    {
        var policy = reliability ?? SctpReliability.Reliable;
        ValidateReliability(policy);
        var created = Stopwatch.GetTimestamp();
        if (data.Length == 0 || data.Length > _options.MaximumMessageSize) throw new ArgumentOutOfRangeException(nameof(data));
        var maximum = (_options.MaximumPacketSize - 28) & ~3;
        var chunks = (data.Length + maximum - 1) / maximum;
        if (chunks > MaximumChunks) throw new ArgumentOutOfRangeException(nameof(data));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                RequireReady();
                linked.Token.ThrowIfCancellationRequested();
                if (admissionGuard != null && !admissionGuard()) throw new InvalidOperationException("The sending channel is closing.");
                if (policy.Mode != DataChannelReliability.Reliable && !SupportsPartialReliability)
                    throw new NotSupportedException("The peer did not negotiate PR-SCTP.");
                if (_closing) throw new InvalidOperationException("SCTP is shutting down.");
                if (streamId >= _outgoingStreams) throw new ArgumentOutOfRangeException(nameof(streamId));
                if (Expired(policy, created)) { _abandonedMessages++; return; }
                if (!_resettingStreams.Contains(streamId) && _sendBytes + data.Length <= _options.SendBufferBytes && _outbound.Count + chunks <= MaximumChunks)
                {
                    if (unchecked(_nextTsn - _initialTsn) > int.MaxValue - (uint)chunks)
                        throw new InvalidOperationException("SCTP serial-number lifetime exhausted; establish a fresh association.");
                    var sequence = unordered ? (ushort)0 : _sendSequences.GetValueOrDefault(streamId);
                    var message = new OutboundMessage(streamId, sequence, unordered, policy, created);
                    for (var offset = 0; offset < data.Length; offset += maximum)
                    {
                        var size = Math.Min(maximum, data.Length - offset);
                        byte flags = (byte)((unordered ? 4 : 0) | (offset == 0 ? 2 : 0) | (offset + size == data.Length ? 1 : 0));
                        var body = new byte[12 + size]; var tsn = _nextTsn++;
                        SctpWire.U32(body, tsn); SctpWire.U16(body.AsSpan(4), streamId); SctpWire.U16(body.AsSpan(6), sequence);
                        SctpWire.U32(body.AsSpan(8), payloadProtocolIdentifier); data.Span.Slice(offset, size).CopyTo(body.AsSpan(12));
                        _outbound.Add(tsn, new(SctpWire.Chunk(0, flags, body), size, message));
                    }
                    if (!unordered) _sendSequences[streamId] = unchecked((ushort)(sequence + 1));
                    _sendBytes += data.Length;
                    if (_drained.Task.IsCompleted) _drained = NewSignal();
                    Wake(); return;
                }
                wait = _space.Task;
            }
            if (policy.Mode == DataChannelReliability.Timed)
            {
                var remaining = TimeSpan.FromMilliseconds(policy.Parameter) - Stopwatch.GetElapsedTime(created);
                if (remaining <= TimeSpan.Zero) { linked.Token.ThrowIfCancellationRequested(); lock (_gate) _abandonedMessages++; return; }
                var timeout = TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1));
                try { await wait.WaitAsync(timeout, linked.Token).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!Expired(policy, created)) continue;
                    lock (_gate) _abandonedMessages++; return;
                }
            }
            else await wait.WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<SctpMessage> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in ReceiveEventsAsync(cancellationToken).ConfigureAwait(false))
            if (item is SctpMessage message) yield return message;
    }
    /// <summary>One serialized reader for data and stream resets; do not also consume ReceiveMessagesAsync.</summary>
    public async IAsyncEnumerable<SctpReceiveEvent> ReceiveEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _receiving, 1, 0) != 0) throw new InvalidOperationException("SCTP permits one receive reader.");
        try
        {
            await foreach (var item in _messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (item is SctpMessage message)
                    { _receiveBytes -= message.Data.Length; _retainedMessages--; _deliveredMessages--; }
                    else { _resetEventReservations--; PulseResetAdmission(); }
                    if (!_receiveEnded || _receiveFailure == null) { FlushDelivery(); AssembleAvailable(); CompletePeerReset(); }
                    CompleteReceiveIfPossible(); _sackNeeded = true;
                }
                Wake(); yield return item;
            }
        }
        finally { Volatile.Write(ref _receiving, 0); }
    }
    public Task DrainAsync(CancellationToken cancellationToken = default)
    { lock (_gate) { RequireReady(); return _drained.Task.WaitAsync(cancellationToken); } }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { RequireReady(); _closing = true; }
        await DrainAsync(cancellationToken).ConfigureAwait(false);
        Task? reset; lock (_gate) reset = _outgoingReset?.Done.Task;
        if (reset != null) await reset.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_shutdownSent && !_shutdownAckSent)
            {
                var body = new byte[4]; SctpWire.U32(body, _receiveTsn);
                SetFlight(SctpWire.Chunk(7, 0, body)); _shutdownSent = true;
            }
        }
        Wake(); await _shutdown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireReady()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_ready || _completion.Task.IsCompleted) throw new InvalidOperationException("SCTP is not established.");
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private async Task RunAsync()
    {
        Exception? reason = null;
        var receive = _transport.ReceiveApplicationDatagramsAsync(_lifetime.Token).GetAsyncEnumerator(_lifetime.Token);
        Task<bool>? next = null;
        async Task ProcessReadyIncomingAsync(bool transportEnded = false)
        {
            lock (_gate) if (_finishAfterFlush) return;
            // Bound each drain by the DTLS application queue capacity. Prefer
            // already authenticated controls to obsolete timer/wakeup output.
            // A completed transport cannot produce more input. Its asynchronous
            // reader may still be scheduled, so await the bounded queued tail.
            for (var count = 0; count < 129 && (transportEnded || next?.IsCompleted == true); count++)
            {
                if (next == null || !await next.ConfigureAwait(false)) throw new IOException("DTLS ended during SCTP.");
                lock (_gate)
                {
                    ProcessPacket(receive.Current);
                    if (_finishAfterFlush) return;
                }
                next = receive.MoveNextAsync().AsTask();
            }
        }
        try
        {
            lock (_gate) if (_role == SctpRole.Initiator) SetFlight(Init(1));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            next = receive.MoveNextAsync().AsTask();
            var tick = timer.WaitForNextTickAsync(_lifetime.Token).AsTask();
            var wake = _wake.WaitAsync(_lifetime.Token);
            while (true)
            {
                await ProcessReadyIncomingAsync().ConfigureAwait(false);
                List<byte[]> packets; bool finish;
                lock (_gate) { packets = PrepareOutgoing(); finish = _finishAfterFlush; }
                try
                {
                    foreach (var packet in packets) await _transport.SendApplicationDatagramAsync(packet, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when ((error is OperationCanceledException or InvalidOperationException or IOException) &&
                    !_lifetime.IsCancellationRequested &&
                    (error is OperationCanceledException || !_transport.IsConnected))
                {
                    // DTLS can consume close-notify while its preceding SCTP
                    // terminal record still awaits this reader. Only verified
                    // SHUTDOWN-COMPLETE makes stale post-shutdown output optional.
                    // Send cancellation precedes DTLS Completion publication.
                    // Join shutdown before draining its final authenticated input.
                    await _transport.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    await ProcessReadyIncomingAsync(transportEnded: true).ConfigureAwait(false);
                    lock (_gate) { if (!_peerShutdownComplete) throw; finish = true; }
                }
                if (finish) break;
                await Task.WhenAny(next, tick, wake).ConfigureAwait(false);
                await ProcessReadyIncomingAsync().ConfigureAwait(false);
                lock (_gate) if (_finishAfterFlush) continue;
                if (tick.IsCompleted)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    lock (_gate) Timers();
                    tick = timer.WaitForNextTickAsync(_lifetime.Token).AsTask();
                }
                if (wake.IsCompleted) { await wake.ConfigureAwait(false); wake = _wake.WaitAsync(_lifetime.Token); }
            }
        }
        catch (Exception error) { reason = error; }
        finally
        {
            _lifetime.Cancel();
            if (next != null) { try { await next.ConfigureAwait(false); } catch (Exception) { } }
            await receive.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                Establishment?.End(EstablishmentPhase.Sctp, reason ?? new IOException("SCTP association closed."));
                _ready = false;
                var failure = reason ?? new IOException("SCTP association closed.");
                _connected.TrySetException(failure); _space.TrySetException(failure); _drained.TrySetException(failure);
                _outgoingReset?.Done.TrySetException(failure); _resetAdmission.TrySetException(failure);
                if (reason == null) _shutdown.TrySetResult(); else _shutdown.TrySetException(reason);
                _completion.TrySetResult(reason);
                _receiveEnded = true;
                _receiveFailure = Volatile.Read(ref _disposed) != 0 ? new ObjectDisposedException(nameof(SctpAssociation)) : reason;
                CompleteReceiveIfPossible();
            }
        }
    }

    private List<byte[]> PrepareOutgoing()
    {
        var result = new List<byte[]>();
        if (_finishAfterFlush)
        {
            // Only a required final SHUTDOWN-COMPLETE may remain. Do not
            // regenerate SACK/reset/expiry work after a terminal control.
            while (_controls.Count != 0)
            {
                var final = _controls.Dequeue();
                if (final[0] == 14) result.Add(Packet(final));
            }
            return result;
        }
        CompletePeerReset(); StartPendingPeerIncomingReset();
        ExpireMessages(); UpdateForwardFlight();
        while (_controls.Count != 0 && result.Count < 32) result.Add(Packet(_controls.Dequeue()));
        if (_sackNeeded && _hasRemoteInit) { _sackNeeded = false; result.Add(Packet(Sack())); }
        if (!_ready || _shutdownAckSent || _finishAfterFlush) return result;
        var flightBytes = _outbound.Values.Where(x => x.Sent && !x.GapAcknowledged && !x.Message.Abandoned).Sum(x => x.Chunk.Length);
        var peerFlightBytes = _outbound.Values.Where(x => x.Sent && !x.GapAcknowledged && !x.Message.Abandoned).Sum(x => x.Size);
        var expedited = false;
        foreach (var pair in _outbound.OrderBy(x => unchecked(x.Key - _acknowledgedTsn)))
        {
            var item = pair.Value;
            if (item.Message.Abandoned || item.GapAcknowledged || (item.Sent && !item.Retransmit)) continue;
            if (item.Sent && item.Retransmit && item.Message.Policy.Mode == DataChannelReliability.RetransmissionLimited &&
                (uint)item.Retransmissions >= item.Message.Policy.Parameter)
            { Abandon(item.Message); continue; }
            var probe = !item.Sent && flightBytes == 0 && _peerWindow == 0 &&
                (_lastProbeAt == 0 || Stopwatch.GetElapsedTime(_lastProbeAt) >= _rto);
            if (_timeoutRecovery && pair.Key != _timeoutRecoveryTsn) continue;
            if (item.Retransmit && expedited && flightBytes > _cwnd) continue;
            if (!item.Retransmit && !probe && (flightBytes + item.Chunk.Length > _cwnd || peerFlightBytes + item.Size > _peerWindow)) break;
            if (item.Retransmit) expedited = true;
            if (result.Count >= 32) { Wake(); break; }
            if (item.Sent)
            {
                if (++item.Retransmissions > _options.MaximumRetransmissions && _peerWindow != 0) throw new TimeoutException("SCTP retransmission limit reached.");
                Interlocked.Increment(ref _retransmissions);
            }
            else { item.Sent = true; _highestSentTsn = pair.Key; flightBytes += item.Chunk.Length; peerFlightBytes += item.Size; }
            item.SentAt = Stopwatch.GetTimestamp(); item.Retransmit = false; item.MissingReports = 0;
            if (probe) _lastProbeAt = item.SentAt;
            result.Add(Packet(item.Chunk));
        }
        return result;
    }

    private byte[] Packet(byte[] chunk) => SctpWire.Packet(_options.LocalPort, _options.RemotePort, chunk[0] == 1 ? 0 : _remoteTag, chunk);
    private void QueueControl(byte[] chunk)
    { if (_controls.Count >= 128) { Reject(); return; } _controls.Enqueue(chunk); }
    private void SetFlight(byte[] chunk)
    { _flight = chunk; _flightAt = Stopwatch.GetTimestamp(); _flightRto = _rto; _flightRetries = 0; QueueControl(chunk); }
    private void Timers()
    {
        if (!_ready && Stopwatch.GetElapsedTime(_startedAt) >= _options.HandshakeTimeout) throw new TimeoutException("SCTP handshake timed out.");
        if (_flight != null && Stopwatch.GetElapsedTime(_flightAt) >= _flightRto)
        {
            if (++_flightRetries > _options.MaximumRetransmissions) throw new TimeoutException("SCTP control retransmission limit reached.");
            QueueControl(_flight); _flightAt = Stopwatch.GetTimestamp(); _flightRto = TimeSpan.FromSeconds(Math.Min(60, 2 * _flightRto.TotalSeconds));
            Interlocked.Increment(ref _retransmissions);
            if (!_ready) Establishment?.Progress(EstablishmentPhase.Sctp, _flight[0] == 10 ? HandshakeStep.SctpCookieAck : HandshakeStep.SctpInitAck, retransmission: true);
        }
        RetryForwardFlight(); RetryResetFlight();
        var oldest = _outbound.Values.Where(x => x.Sent && !x.GapAcknowledged && !x.Message.Abandoned).OrderBy(x => x.SentAt).FirstOrDefault();
        if (oldest != null && !oldest.Retransmit && Stopwatch.GetElapsedTime(oldest.SentAt) >= _rto)
        {
            _threshold = Math.Max(_cwnd / 2, 4 * _maximumChunkSize); _cwnd = _maximumChunkSize; _partialAcked = 0;
            _fastRecovery = false; _timeoutRecovery = true; _timeoutRecoveryTsn = _outbound.First(x => ReferenceEquals(x.Value, oldest)).Key;
            _rto = TimeSpan.FromSeconds(Math.Min(60, _rto.TotalSeconds * 2)); oldest.Retransmit = true;
        }
        if (_peerShutdown && _outbound.Count == 0 && !_shutdownAckSent)
        { _shutdownAckSent = true; SetFlight(SctpWire.Chunk(8, 0, [])); }
    }

    private byte[] Init(byte type, byte[]? cookie = null)
    {
        var extensionCount = (_options.EnablePartialReliability ? 1 : 0) + (_options.EnableStreamReset ? 1 : 0);
        var legacyLength = _options.EnablePartialReliability ? 4 : 0;
        var cookieLength = cookie == null ? 0 : 4 + cookie.Length;
        var body = new byte[16 + cookieLength + legacyLength + (extensionCount != 0 ? 8 : 0)];
        SctpWire.U32(body, _localTag); SctpWire.U32(body.AsSpan(4), (uint)_options.ReceiveBufferBytes);
        SctpWire.U16(body.AsSpan(8), type == 2 ? _outgoingStreams : _options.Streams); SctpWire.U16(body.AsSpan(10), _options.Streams); SctpWire.U32(body.AsSpan(12), _initialTsn);
        if (cookie != null) { SctpWire.U16(body.AsSpan(16), 7); SctpWire.U16(body.AsSpan(18), (ushort)(4 + cookie.Length)); cookie.CopyTo(body, 20); }
        var offset = 16 + cookieLength;
        if (_options.EnablePartialReliability)
        { SctpWire.U16(body.AsSpan(offset), 0xc000); SctpWire.U16(body.AsSpan(offset + 2), 4); offset += 4; }
        if (extensionCount != 0)
        {
            SctpWire.U16(body.AsSpan(offset), 0x8008); SctpWire.U16(body.AsSpan(offset + 2), (ushort)(4 + extensionCount));
            var value = offset + 4;
            if (_options.EnablePartialReliability) body[value++] = 192;
            if (_options.EnableStreamReset) body[value] = 130;
        }
        return SctpWire.Chunk(type, 0, body);
    }

    private void ProcessPacket(byte[] bytes)
    {
        if (!SctpPacket.TryParse(bytes, out var packet) || packet.SourcePort != _options.RemotePort || packet.DestinationPort != _options.LocalPort)
        { Reject(); return; }
        var chunks = packet.Chunks; chunks.MoveNext(); var first = chunks.Current;
        if (first.Type == 1)
        {
            if (packet.VerificationTag != 0 || chunks.MoveNext() || first.Flags != 0) { Reject(); return; }
            ProcessInit(first.Body, false); return;
        }
        if (packet.VerificationTag != _localTag)
        {
            if (first.Type is 6 or 14 && (first.Flags & 1) != 0 && packet.VerificationTag == _remoteTag)
            { if (first.Type == 6) throw new IOException("Peer aborted SCTP."); _finishAfterFlush = true; }
            else Reject();
            return;
        }
        foreach (var chunk in packet.Chunks)
        {
            switch (chunk.Type)
            {
                case 2 when !_ready && _role == SctpRole.Initiator: ProcessInit(chunk.Body, true); break;
                case 10:
                    if (_cookie == null || !CryptographicOperations.FixedTimeEquals(chunk.Body, _cookie)) { Reject(); break; }
                    QueueControl(SctpWire.Chunk(11, 0, [])); Establish(); break;
                case 11 when !_ready && _peerCookie != null && chunk.Body.IsEmpty: Establish(); break;
                case 0 when _ready: ProcessData(chunk.Flags, chunk.Body); break;
                case 3 when _ready: ProcessSack(chunk.Body); break;
                case 192 when _ready && SupportsPartialReliability: ProcessForwardTsn(chunk.Body); break;
                case 130 when _ready && SupportsStreamReset: ProcessReconfiguration(chunk.Body); break;
                case 4 when _ready:
                    if (chunk.Body.Length < 4 || chunk.Body.Length + 16 > _options.MaximumPacketSize ||
                        SctpWire.U16(chunk.Body) != 1 || SctpWire.U16(chunk.Body[2..]) != chunk.Body.Length) { Reject(); break; }
                    QueueControl(SctpWire.Chunk(5, 0, chunk.Body)); break;
                case 5: break;
                case 6: throw new IOException("Peer aborted SCTP.");
                case 7 when _ready && chunk.Body.Length == 4:
                    Acknowledge(SctpWire.U32(chunk.Body)); _closing = _peerShutdown = true;
                    if (_outbound.Count == 0) { _shutdownAckSent = true; SetFlight(SctpWire.Chunk(8, 0, [])); }
                    break;
                case 8 when _shutdownSent && chunk.Flags == 0 && chunk.Body.IsEmpty:
                    _flight = null; _controls.Clear(); _sackNeeded = false;
                    QueueControl(SctpWire.Chunk(14, 0, [])); _finishAfterFlush = true; return;
                case 14 when _shutdownAckSent && chunk.Flags == 0 && chunk.Body.IsEmpty:
                    _flight = null; _controls.Clear(); _sackNeeded = false; _peerShutdownComplete = _finishAfterFlush = true; return;
                case 9: break; // ERROR is diagnostic, not an instruction to change negotiated features.
                default:
                    Reject();
                    if ((chunk.Type & 0x40) != 0)
                    {
                        var error = new byte[8 + chunk.Body.Length]; SctpWire.U16(error, 6); SctpWire.U16(error.AsSpan(2), (ushort)error.Length);
                        error[4] = chunk.Type; error[5] = chunk.Flags; SctpWire.U16(error.AsSpan(6), (ushort)(chunk.Body.Length + 4)); chunk.Body.CopyTo(error.AsSpan(8));
                        if (error.Length + 16 <= _options.MaximumPacketSize) QueueControl(SctpWire.Chunk(9, 0, error));
                    }
                    if ((chunk.Type & 0x80) == 0) return;
                    break;
            }
        }
    }

    private void ProcessInit(ReadOnlySpan<byte> body, bool acknowledgment)
    {
        if (body.Length < 16 || SctpWire.U32(body) == 0 || SctpWire.U32(body[4..]) < 1500 ||
            SctpWire.U16(body[8..]) == 0 || SctpWire.U16(body[10..]) == 0 || (acknowledgment && SctpWire.U16(body[8..]) > _options.Streams))
        { Reject(); return; }
        byte[]? cookie = null; var parameters = body[16..]; var partialReliability = false; var streamReset = false;
        var forwardParameter = false; var extensionsParameter = false;
        while (!parameters.IsEmpty)
        {
            if (parameters.Length < 4) { Reject(); return; }
            var type = SctpWire.U16(parameters); var length = SctpWire.U16(parameters[2..]); var padded = (length + 3) & ~3;
            if (length < 4 || length > parameters.Length || (padded > parameters.Length && length != parameters.Length)) { Reject(); return; }
            if (type == 7)
            { if (!acknowledgment || cookie != null || length is < 5 or > 1024) { Reject(); return; } cookie = parameters.Slice(4, length - 4).ToArray(); }
            else if (type == 0xc000)
            { if (length != 4 || forwardParameter) { Reject(); return; } forwardParameter = partialReliability = true; }
            else if (type == 0x8008)
            {
                if (length > 260 || extensionsParameter) { Reject(); return; }
                extensionsParameter = true;
                if (parameters.Slice(4, length - 4).Contains((byte)192)) partialReliability = true;
                if (parameters.Slice(4, length - 4).Contains((byte)130)) streamReset = true;
            }
            else if (type is not (5 or 6 or 9 or 11 or 12) && (type & 0x8000) == 0) { Reject(); return; }
            parameters = padded >= parameters.Length ? [] : parameters[padded..];
        }
        if (acknowledgment && (cookie == null || cookie.Length + 16 > _options.MaximumPacketSize)) { Reject(); return; }
        var tag = SctpWire.U32(body); var tsn = SctpWire.U32(body[12..]);
        if (_hasRemoteInit && (tag != _remoteTag || tsn != _remoteInitialTsn)) { Reject(); return; }
        if (_ready && (partialReliability != _peerPartialReliability || streamReset != _peerStreamReset)) { Reject(); return; }
        _peerPartialReliability = partialReliability; _peerStreamReset = streamReset;
        if (!_hasRemoteInit)
        {
            _remoteTag = tag; _remoteInitialTsn = tsn; _nextPeerResetSequence = tsn; _receiveTsn = unchecked(tsn - 1);
            _peerWindow = SctpWire.U32(body[4..]); _threshold = (int)Math.Min(int.MaxValue, Math.Max(_peerWindow, (uint)(4 * _options.MaximumPacketSize)));
            _outgoingStreams = Math.Min(_options.Streams, SctpWire.U16(body[10..]));
            _incomingStreams = Math.Min(_options.Streams, SctpWire.U16(body[8..])); _hasRemoteInit = true;
        }
        if (acknowledgment)
        {
            _peerCookie = cookie;
            Establishment?.Progress(EstablishmentPhase.Sctp, HandshakeStep.SctpCookieAck);
            SetFlight(SctpWire.Chunk(10, 0, cookie!));
        }
        else
        {
            if (_cookie == null)
            {
                var seed = new byte[24]; body[..16].CopyTo(seed); SctpWire.U32(seed.AsSpan(16), _localTag); SctpWire.U32(seed.AsSpan(20), _initialTsn);
                _cookie = HMACSHA256.HashData(_cookieKey, seed); _initAck = Init(2, _cookie);
            }
            Establishment?.Progress(EstablishmentPhase.Sctp, HandshakeStep.SctpCookieEcho);
            QueueControl(_initAck!);
        }
    }

    private void Establish()
    {
        if (!_hasRemoteInit) throw new IOException("SCTP cookie without negotiated parameters.");
        if (_ready) return;
        _flight = null; _ready = true;
        Establishment?.Progress(EstablishmentPhase.Sctp, HandshakeStep.SctpComplete);
        Establishment?.End(EstablishmentPhase.Sctp); _connected.TrySetResult();
    }

    private void ProcessData(byte flags, ReadOnlySpan<byte> body)
    {
        if (body.Length < 13 || (flags & ~15) != 0) { Reject(); return; }
        var tsn = SctpWire.U32(body); _sackNeeded = true;
        if (!SctpWire.After(tsn, _receiveTsn) || _gaps.Contains(tsn)) return;
        var stream = SctpWire.U16(body[4..]); var sequence = SctpWire.U16(body[6..]); var ppid = SctpWire.U32(body[8..]);
        if (stream >= _incomingStreams) { Reject(); return; }
        // RFC 9260 section 6.2: when byte storage is exhausted, a gap-filling
        // DATA chunk may replace the highest undelivered, gap-acknowledged TSNs.
        // Never revoke cumulative acknowledgments or messages released to the ULP.
        if (tsn == unchecked(_receiveTsn + 1) && (_receiveBytes + body.Length - 12 > _options.ReceiveBufferBytes ||
            _fragments.Count + _retainedMessages >= MaximumChunks))
        {
            foreach (var held in _gaps.Where(_fragments.ContainsKey).OrderByDescending(value => unchecked(value - _receiveTsn)).ToArray())
            {
                if (_receiveBytes + body.Length - 12 <= _options.ReceiveBufferBytes && _fragments.Count + _retainedMessages < MaximumChunks) break;
                _receiveBytes -= _fragments[held].Data.Length; _fragments.Remove(held); _gaps.Remove(held);
            }
        }
        if (unchecked(tsn - _receiveTsn) > MaximumChunks || _fragments.Count + _retainedMessages >= MaximumChunks ||
            _gaps.Count >= MaximumChunks || _receiveBytes + body.Length - 12 > _options.ReceiveBufferBytes ||
            _retainedMessages >= MaximumChunks ||
            (_retainedMessages >= _options.MaximumQueuedMessages && tsn != unchecked(_receiveTsn + 1))) { Reject(); return; }
        _fragments.Add(tsn, new(stream, sequence, ppid, flags, body[12..].ToArray()));
        _receiveBytes += body.Length - 12; _gaps.Add(tsn);
        while (_gaps.Remove(unchecked(_receiveTsn + 1))) _receiveTsn++;
        AssembleAvailable(); CompletePeerReset();
    }

    private void AssembleAvailable()
    {
        foreach (var start in _fragments.Where(x => (x.Value.Flags & 2) != 0).Select(x => x.Key).ToArray())
        {
            if (_retainedMessages >= MaximumChunks) break;
            var first = _fragments[start];
            if (_peerOutgoingReset is { } reset && reset.Streams.Contains(first.Stream) && SctpWire.After(start, reset.Barrier)) continue;
            var size = 0; var count = 0; var tsn = start; var complete = false;
            while (_fragments.TryGetValue(tsn, out var fragment))
            {
                if (fragment.Stream != first.Stream || fragment.Sequence != first.Sequence || fragment.Ppid != first.Ppid ||
                    ((fragment.Flags ^ first.Flags) & 4) != 0 || (count != 0 && (fragment.Flags & 2) != 0))
                    throw new IOException("Conflicting SCTP message fragments.");
                size += fragment.Data.Length;
                if (size > _options.MaximumMessageSize || ++count > MaximumChunks) throw new IOException("SCTP message bound exceeded.");
                if ((fragment.Flags & 1) != 0) { complete = true; break; } tsn++;
            }
            if (!complete) continue;
            // Leave later ordered messages in fragment storage while their TSNs
            // are gap-acknowledged, so full-buffer gap filling can safely renege.
            if ((first.Flags & 4) == 0 && SctpWire.After(first.Sequence, _receiveSequences.GetValueOrDefault(first.Stream)) &&
                SctpWire.After(tsn, _receiveTsn)) continue;
            var bytes = new byte[size]; var offset = 0;
            for (var i = 0; i < count; i++) { var f = _fragments[unchecked(start + (uint)i)]; f.Data.CopyTo(bytes, offset); offset += f.Data.Length; _fragments.Remove(unchecked(start + (uint)i)); }
            var message = new SctpMessage(first.Stream, first.Ppid, (first.Flags & 4) != 0, bytes);
            _retainedMessages++;
            if (message.Unordered) _unorderedPending.Enqueue(message);
            else
            {
                var expected = _receiveSequences.GetValueOrDefault(message.StreamId);
                if (message.Data.Length != 0 && SctpWire.After(expected, first.Sequence))
                { _receiveBytes -= message.Data.Length; _retainedMessages--; continue; }
                if (unchecked((ushort)(first.Sequence - expected)) >= MaximumChunks)
                    throw new IOException("SCTP ordered-message lookahead exceeded.");
                if (!_ordered.TryGetValue(message.StreamId, out var queue)) _ordered.Add(message.StreamId, queue = []);
                if (!queue.TryAdd(first.Sequence, message)) throw new IOException("Duplicate SCTP stream sequence.");
                _receiveSequences[message.StreamId] = expected;
            }
        }
        FlushDelivery();
    }
    private void FlushDelivery()
    {
        while (_unorderedPending.Count != 0 && _deliveredMessages < _options.MaximumQueuedMessages)
            Deliver(_unorderedPending.Dequeue());
        foreach (var (stream, queue) in _ordered)
        {
            var expected = _receiveSequences.GetValueOrDefault(stream);
            while (_deliveredMessages < _options.MaximumQueuedMessages)
            {
                if (queue.Remove(expected, out var ready)) { Deliver(ready); expected++; }
                else if (_forwardSequences.TryGetValue(stream, out var skipped) && !SctpWire.After(expected, skipped)) expected++;
                else break;
            }
            _receiveSequences[stream] = expected;
            if (_forwardSequences.TryGetValue(stream, out var through) && SctpWire.After(expected, through)) _forwardSequences.Remove(stream);
        }
    }
    private void Deliver(SctpMessage message)
    {
        if (!_messages.Writer.TryWrite(message)) throw new IOException("SCTP delivery accounting invariant failed.");
        _deliveredMessages++;
    }

    private void CompleteReceiveIfPossible()
    {
        if (!_receiveEnded) return;
        if (_receiveFailure != null)
        {
            _messages.Writer.TryComplete(_receiveFailure is ObjectDisposedException ? null : _receiveFailure);
            return;
        }
        var pending = _fragments.Count != 0 || _unorderedPending.Count != 0 || _ordered.Values.Any(queue => queue.Count != 0);
        if (!pending) _messages.Writer.TryComplete();
        else if (_deliveredMessages == 0)
            _messages.Writer.TryComplete(new IOException("SCTP closed with incomplete or unsequenced messages."));
    }

    private byte[] Sack()
    {
        var offsets = _gaps.Select(tsn => unchecked(tsn - _receiveTsn)).Order().ToArray();
        var ranges = new List<(ushort Start, ushort End)>();
        foreach (var offset in offsets)
        {
            if (ranges.Count != 0 && ranges[^1].End + 1 == offset) ranges[^1] = (ranges[^1].Start, (ushort)offset);
            else ranges.Add(((ushort)offset, (ushort)offset));
            if (ranges.Count >= (_options.MaximumPacketSize - 28) / 4) break;
        }
        var body = new byte[12 + ranges.Count * 4]; SctpWire.U32(body, _receiveTsn);
        var window = _retainedMessages >= _options.MaximumQueuedMessages ? 0 : _options.ReceiveBufferBytes - _receiveBytes;
        SctpWire.U32(body.AsSpan(4), (uint)window); SctpWire.U16(body.AsSpan(8), (ushort)ranges.Count);
        for (var i = 0; i < ranges.Count; i++) { SctpWire.U16(body.AsSpan(12 + 4 * i), ranges[i].Start); SctpWire.U16(body.AsSpan(14 + 4 * i), ranges[i].End); }
        return SctpWire.Chunk(3, 0, body);
    }

    private void ProcessSack(ReadOnlySpan<byte> body)
    {
        if (body.Length < 12) { Reject(); return; }
        var cumulative = SctpWire.U32(body); var count = SctpWire.U16(body[8..]); var duplicates = SctpWire.U16(body[10..]);
        if (body.Length != 12 + 4 * (count + duplicates) || count > MaximumChunks ||
            SctpWire.After(cumulative, _highestSentTsn)) { Reject(); return; }
        if (SctpWire.After(_acknowledgedTsn, cumulative)) return;
        var ranges = new (ushort Start, ushort End)[count]; ushort last = 0;
        for (var i = 0; i < count; i++)
        {
            var start = SctpWire.U16(body[(12 + 4 * i)..]); var end = SctpWire.U16(body[(14 + 4 * i)..]);
            if (start == 0 || start > end || start <= last || SctpWire.After(unchecked(cumulative + end), _highestSentTsn)) { Reject(); return; }
            ranges[i] = (start, end); last = end;
        }
        var oldFlight = _outbound.Values.Where(x => x.Sent && !x.GapAcknowledged && !x.Message.Abandoned).Sum(x => x.Chunk.Length);
        var fullyUsed = oldFlight + _maximumChunkSize >= _cwnd;
        var newlyAcked = Acknowledge(cumulative);
        var top = unchecked(cumulative + last); var newTop = SctpWire.After(top, _lastGapTop); if (newTop) _lastGapTop = top;
        var newFastRetransmission = false;
        foreach (var pair in _outbound)
        {
            var item = pair.Value; if (!item.Sent || item.Message.Abandoned) continue;
            var offset = unchecked(pair.Key - cumulative);
            var gap = ranges.Any(range => offset >= range.Start && offset <= range.End);
            if (gap && !item.GapAcknowledged)
            {
                newlyAcked += item.Chunk.Length;
                if (item.Retransmissions == 0 && !item.RttSampled)
                { MeasureRtt(Stopwatch.GetElapsedTime(item.SentAt).TotalSeconds); item.RttSampled = true; }
            }
            if (_timeoutRecovery && pair.Key == _timeoutRecoveryTsn && gap) _timeoutRecovery = false;
            if (!gap && item.GapAcknowledged) item.Retransmit = true;
            item.GapAcknowledged = gap;
            if (!gap && !item.FastRetransmitted && newTop && SctpWire.After(top, pair.Key) && ++item.MissingReports >= 3)
            { item.Retransmit = item.FastRetransmitted = true; newFastRetransmission = true; }
        }
        _peerWindow = SctpWire.U32(body[4..]);
        if (_fastRecovery && !SctpWire.After(_fastRecoveryExit, cumulative)) _fastRecovery = false;
        if (newlyAcked > 0 && fullyUsed && !_fastRecovery)
        {
            if (_cwnd <= _threshold) _cwnd = Math.Min(_options.SendBufferBytes, _cwnd + Math.Min(newlyAcked, _maximumChunkSize));
            else { _partialAcked += newlyAcked; if (_partialAcked >= _cwnd) { _partialAcked -= _cwnd; _cwnd = Math.Min(_options.SendBufferBytes, _cwnd + _maximumChunkSize); } }
        }
        if (newFastRetransmission && !_fastRecovery)
        { _threshold = Math.Max(_cwnd / 2, 4 * _maximumChunkSize); _cwnd = _threshold; _partialAcked = 0; _fastRecovery = true; _fastRecoveryExit = _highestSentTsn; }
    }

    private int Acknowledge(uint cumulative)
    {
        if (SctpWire.After(cumulative, _highestSentTsn)) { Reject(); return 0; }
        if (SctpWire.After(_acknowledgedTsn, cumulative)) return 0;
        var bytes = 0; var removed = 0;
        foreach (var tsn in _outbound.Keys.Where(tsn => !SctpWire.After(tsn, cumulative)).ToArray())
        {
            var item = _outbound[tsn];
            if (!item.Sent && !item.Message.Abandoned) throw new IOException("Peer acknowledged unsent SCTP data.");
            if (!item.Message.Abandoned)
            {
                if (!item.GapAcknowledged) bytes += item.Chunk.Length;
                if (item.Retransmissions == 0 && !item.RttSampled) MeasureRtt(Stopwatch.GetElapsedTime(item.SentAt).TotalSeconds);
                _sendBytes -= item.Size;
            }
            _outbound.Remove(tsn); removed++;
        }
        _acknowledgedTsn = cumulative;
        if (_forwardFlight != null && !SctpWire.After(_forwardPoint, cumulative)) _forwardFlight = null;
        if (_timeoutRecovery && !SctpWire.After(_timeoutRecoveryTsn, cumulative)) _timeoutRecovery = false;
        if (_outbound.Count == 0) _drained.TrySetResult();
        if (removed != 0) { _space.TrySetResult(); _space = NewSignal(); }
        return bytes;
    }
    private void MeasureRtt(double seconds)
    {
        if (_smoothedRtt == 0) { _smoothedRtt = seconds; _rttVariance = seconds / 2; }
        else { _rttVariance = .75 * _rttVariance + .25 * Math.Abs(_smoothedRtt - seconds); _smoothedRtt = .875 * _smoothedRtt + .125 * seconds; }
        _rto = TimeSpan.FromSeconds(Math.Clamp(_smoothedRtt + 4 * _rttVariance, _options.InitialRetransmissionTimeout.TotalSeconds, 60));
    }
    private void Reject() => Interlocked.Increment(ref _rejected);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_pump != null) await _pump.ConfigureAwait(false);
        else { _completion.TrySetResult(new ObjectDisposedException(nameof(SctpAssociation))); _messages.Writer.TryComplete(); }
        lock (_gate)
        {
            _receiveEnded = true; _receiveFailure = new ObjectDisposedException(nameof(SctpAssociation));
            CompleteReceiveIfPossible();
            _outbound.Clear(); _fragments.Clear(); _gaps.Clear(); _ordered.Clear(); _controls.Clear(); _unorderedPending.Clear(); _forwardSequences.Clear(); _resetCache.Clear(); _resettingStreams.Clear();
            CryptographicOperations.ZeroMemory(_cookieKey); if (_cookie != null) CryptographicOperations.ZeroMemory(_cookie);
        }
        _lifetime.Dispose();
    }
    private sealed class Outbound(byte[] chunk, int size, OutboundMessage message)
    {
        internal byte[] Chunk = chunk;
        internal readonly int Size = size;
        internal readonly OutboundMessage Message = message;
        internal long SentAt;
        internal bool Sent, GapAcknowledged, Retransmit, FastRetransmitted, RttSampled;
        internal int Retransmissions, MissingReports;
    }
    private sealed record Fragment(ushort Stream, ushort Sequence, uint Ppid, byte Flags, byte[] Data);
}
