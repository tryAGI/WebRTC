using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public sealed record DataChannelLimits
{
    public int MaximumChannels { get; init; } = 64;
    public int MaximumQueuedMessages { get; init; } = 128;
    public int ReceiveBufferBytes { get; init; } = 1024 * 1024;
}

/// <summary>Bounded DCEP channels and stream closure. Owns the SCTP message reader, not the association lifetime.</summary>
public sealed class DataChannelAssociation : IAsyncDisposable
{
    internal EstablishmentJournal? Establishment { get; init; }
    private readonly SctpAssociation _sctp;
    private readonly DataChannelLimits _limits;
    private readonly object _gate = new();
    private readonly Dictionary<ushort, DataChannel> _channels = [];
    // One peer generation per reserved ID; its messages share the aggregate receive budget.
    private readonly Dictionary<ushort, DataChannel> _pendingChannels = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<DataChannel> _accepted;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _space = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private int _queuedBytes, _queuedMessages, _disposed;
    internal int MaximumMessageSize => _sctp.MaximumMessageSize;
    public Task<Exception?> Completion => _completion.Task;

    public DataChannelAssociation(SctpAssociation sctp, DataChannelLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(sctp); _limits = limits ?? new();
        if (_limits.MaximumChannels is < 1 or > 128 || _limits.MaximumQueuedMessages is < 1 or > 1024 ||
            _limits.ReceiveBufferBytes is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limits));
        if (!sctp.IsConnected) throw new InvalidOperationException("Data channels require an established SCTP association.");
        _sctp = sctp;
        _accepted = Channel.CreateBounded<DataChannel>(new BoundedChannelOptions(_limits.MaximumChannels)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        _pump = RunAsync();
    }

    public Task<DataChannel> OpenChannelAsync(string label, bool ordered = true, string protocol = "", ushort priority = 256,
        CancellationToken cancellationToken = default) =>
        OpenChannelAsync(new(label, protocol, ordered, DataChannelReliability.Reliable, 0, priority), cancellationToken);

    public async Task<DataChannel> OpenChannelAsync(DataChannelParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Reliability == DataChannelReliability.Reliable) parameters = parameters with { ReliabilityParameter = 0 };
        var open = DataChannelProtocol.EncodeOpen(parameters); DataChannel channel;
        if (parameters.Reliability != DataChannelReliability.Reliable && !_sctp.SupportsPartialReliability)
            throw new NotSupportedException("The peer did not negotiate PR-SCTP.");
        lock (_gate)
        {
            RequireRunning();
            if (_channels.Count >= _limits.MaximumChannels) throw new InvalidOperationException("Data channel limit reached.");
            var parity = _sctp.DtlsRole == DtlsRole.Client ? 0 : 1;
            var streamLimit = Math.Min(_sctp.OutgoingStreams, _sctp.IncomingStreams);
            var selected = -1;
            for (var stream = parity; stream < streamLimit; stream += 2) if (!_channels.ContainsKey((ushort)stream)) { selected = stream; break; }
            if (selected < 0) throw new InvalidOperationException("No available data-channel stream.");
            channel = new(this, (ushort)selected, parameters, _limits.MaximumQueuedMessages); _channels.Add(channel.StreamId, channel);
        }
        var sent = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var operation = Establishment?.Begin(EstablishmentPhase.Dcep, HandshakeStep.DcepAck) ?? 0;
        try
        {
            await _sctp.SendMessageAsync(channel.StreamId, 50, open, cancellationToken: linked.Token).ConfigureAwait(false); sent = true;
            await channel.Opened.WaitAsync(linked.Token).ConfigureAwait(false);
            Establishment?.End(EstablishmentPhase.Dcep, operation: operation); return channel;
        }
        catch (Exception error)
        {
            Establishment?.End(EstablishmentPhase.Dcep, error, operation);
            lock (_gate)
            {
                channel.OpeningFailure = error; DiscardChannelCore(channel);
                if (!sent) { _channels.Remove(channel.StreamId); channel.End(error); }
                else if (_sctp.SupportsStreamReset) StartChannelReset(channel);
                else channel.End(error); // Retire the ID when the peer cannot reset it.
            }
            throw;
        }
    }
    public IAsyncEnumerable<DataChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default) => _accepted.Reader.ReadAllAsync(cancellationToken);
    internal async ValueTask SendAsync(DataChannel channel, uint ppid, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        lock (_gate) RequireRunning();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _sctp.SendMessageCoreAsync(channel.StreamId, ppid, bytes, !channel.Parameters.Ordered, linked.Token,
            new(channel.Parameters.Reliability, channel.Parameters.ReliabilityParameter), () => channel.IsOpen).ConfigureAwait(false);
    }
    internal async Task CloseChannelAsync(DataChannel channel, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!channel.Completion.IsCompleted)
            {
                RequireRunning();
                if (!_sctp.SupportsStreamReset) throw new NotSupportedException("The peer did not negotiate stream reset.");
                channel.BeginClosing(); StartChannelReset(channel);
            }
        }
        var error = await channel.Completion.WaitAsync(ct).ConfigureAwait(false);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    internal void DiscardChannel(DataChannel channel)
    {
        lock (_gate)
        {
            DiscardChannelCore(channel);
            if (channel.Completion.IsCompleted) return;
            if (_disposed != 0 || _completion.Task.IsCompleted) channel.End(new ObjectDisposedException(nameof(DataChannelAssociation)));
            else if (_sctp.SupportsStreamReset) StartChannelReset(channel);
            else channel.End(new NotSupportedException("The peer cannot reset the discarded channel; its ID remains retired."));
        }
    }
    private void DiscardChannelCore(DataChannel channel)
    {
        _queuedMessages -= channel.DiscardBuffered(out var bytes); _queuedBytes -= bytes;
        _space.TrySetResult(); _space = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private void StartChannelReset(DataChannel channel)
    {
        if (channel.ResetTask == null && !channel.OutgoingReset) channel.ResetTask = ResetChannelAsync(channel);
    }
    private async Task ResetChannelAsync(DataChannel channel)
    {
        try { await _sctp.ResetOutgoingStreamsAsync(new ushort[] { channel.StreamId }, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate)
            {
                channel.DiscardIncoming = true; channel.End(error);
                if (_pendingChannels.Remove(channel.StreamId, out var pending))
                { DiscardChannelCore(pending); pending.End(error); }
            } // Preserve acknowledged messages and keep the ID reserved when reset fails.
        }
    }
    private async Task ProcessResetAsync(SctpStreamReset reset)
    {
        List<DataChannel> admitted = [];
        lock (_gate)
        {
            foreach (var stream in reset.StreamIds)
            {
                if (!_channels.TryGetValue(stream, out var channel)) continue;
                channel.BeginClosing();
                if (reset.Outgoing) channel.OutgoingReset = true;
                else
                {
                    if (channel.IncomingReset) throw new IOException("A further peer generation reset arrived before confirmation.");
                    channel.IncomingReset = true; StartChannelReset(channel);
                }
                if (channel.IncomingReset && channel.OutgoingReset)
                {
                    channel.End(channel.OpeningFailure); _channels.Remove(stream);
                    if (_pendingChannels.Remove(stream, out var pending))
                    { _channels.Add(stream, pending); admitted.Add(pending); }
                }
            }
        }
        foreach (var channel in admitted) await AcknowledgeAcceptedAsync(channel).ConfigureAwait(false);
    }
    internal void ReleaseMessage(int bytes)
    {
        lock (_gate)
        { _queuedBytes -= bytes; _queuedMessages--; _space.TrySetResult(); _space = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    }
    private void RequireRunning()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_completion.Task.IsCompleted) throw new InvalidOperationException("Data-channel association ended.");
    }
    private async Task RunAsync()
    {
        Exception? reason = null;
        try
        {
            await foreach (var item in _sctp.ReceiveEventsAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (item is SctpStreamReset reset) { await ProcessResetAsync(reset).ConfigureAwait(false); continue; }
                var message = (SctpMessage)item;
                if (message.PayloadProtocolIdentifier == 50) { await ProcessDcepAsync(message).ConfigureAwait(false); continue; }
                DataChannel channel; bool pending;
                lock (_gate)
                {
                    pending = _pendingChannels.TryGetValue(message.StreamId, out channel!);
                    if (!pending && (!_channels.TryGetValue(message.StreamId, out channel!) || (!channel.CanReceive && !channel.DiscardIncoming)))
                        throw new IOException("User data on an unopened channel.");
                }
                var ppid = message.PayloadProtocolIdentifier;
                if (ppid is not (51 or 53 or 56 or 57)) throw new IOException("Unsupported WebRTC payload identifier.");
                var empty = ppid is 56 or 57;
                if (empty && message.Data.Length != 1) throw new IOException("Invalid empty data-channel message.");
                var bytes = empty ? Array.Empty<byte>() : message.Data;
                var text = ppid is 51 or 56;
                if (text) DataChannelProtocol.Utf8.GetCharCount(bytes);
                if (bytes.Length > _limits.ReceiveBufferBytes) throw new IOException("Data-channel receive budget exceeded.");
                while (true)
                {
                    Task wait;
                    lock (_gate)
                    {
                        if (channel.DiscardIncoming) break;
                        if (_queuedMessages < _limits.MaximumQueuedMessages && _queuedBytes + bytes.Length <= _limits.ReceiveBufferBytes)
                        { _queuedMessages++; _queuedBytes += bytes.Length; channel.Deliver(new(text ? DataChannelMessageKind.Text : DataChannelMessageKind.Binary, bytes)); break; }
                        // Waiting here would block the outgoing reset event needed to admit this generation.
                        if (pending) throw new IOException("Pending data-channel generation receive budget exceeded.");
                        wait = _space.Task;
                    }
                    await wait.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { reason = error; }
        finally
        {
            lock (_gate)
            {
                var channelFailure = reason ?? (_lifetime.IsCancellationRequested ? new ObjectDisposedException(nameof(DataChannelAssociation)) : null);
                foreach (var channel in _channels.Values) channel.End(channelFailure);
                foreach (var channel in _pendingChannels.Values)
                { DiscardChannelCore(channel); channel.End(channelFailure); }
                _pendingChannels.Clear();
                _accepted.Writer.TryComplete(reason); _space.TrySetException(reason ?? new IOException("Data channels closed."));
                _completion.TrySetResult(reason);
            }
        }
    }
    private async Task ProcessDcepAsync(SctpMessage message)
    {
        if (DataChannelProtocol.IsAcknowledgment(message.Data))
        {
            lock (_gate)
            {
                var parity = _sctp.DtlsRole == DtlsRole.Client ? 0 : 1;
                if ((message.StreamId & 1) != parity || !_channels.TryGetValue(message.StreamId, out var existing))
                    throw new IOException("DCEP ACK without local OPEN.");
                // Pinned Chromium sends an unordered canonical ACK for unordered channels.
                // Bound this interoperability exception to that local channel and exact message.
                if (message.Unordered && (existing.Parameters.Ordered || message.Data.Length != 1))
                    throw new IOException("Unexpected unordered DCEP acknowledgment.");
                existing.Acknowledge();
            }
            return;
        }
        if (message.Unordered) throw new IOException("DCEP OPEN requires ordered reliable delivery.");
        if (!DataChannelProtocol.TryParseOpen(message.Data, out var parameters)) throw new IOException("Malformed DCEP OPEN.");
        if (parameters!.Reliability != DataChannelReliability.Reliable && !_sctp.SupportsPartialReliability)
            throw new IOException("Partial reliability was not negotiated.");
        var localParity = _sctp.DtlsRole == DtlsRole.Client ? 0 : 1;
        if ((message.StreamId & 1) == localParity || message.StreamId >= _sctp.OutgoingStreams)
            throw new IOException("Invalid DCEP stream parity or bounds.");
        DataChannel channel;
        lock (_gate)
        {
            if (_channels.TryGetValue(message.StreamId, out var old))
            {
                if (!old.IncomingReset || old.OutgoingReset || old.Completion.IsCompleted || _pendingChannels.ContainsKey(message.StreamId))
                    throw new IOException("DCEP channel bound or duplicate OPEN.");
                // The peer has reset its sending direction, but our explicit reset result can be lost/reordered.
                // Retain the reserved ID and defer ACK/public admission until that result is received.
                _pendingChannels.Add(message.StreamId, new(this, message.StreamId, parameters, _limits.MaximumQueuedMessages));
                return;
            }
            if (_channels.Count >= _limits.MaximumChannels) throw new IOException("DCEP channel bound or duplicate OPEN.");
            channel = new(this, message.StreamId, parameters, _limits.MaximumQueuedMessages); _channels.Add(message.StreamId, channel);
        }
        await AcknowledgeAcceptedAsync(channel).ConfigureAwait(false);
    }
    private async Task AcknowledgeAcceptedAsync(DataChannel channel)
    {
        await _sctp.SendMessageAsync(channel.StreamId, 50, new byte[] { 2 }, cancellationToken: _lifetime.Token).ConfigureAwait(false);
        channel.Acknowledge();
        if (!_accepted.Writer.TryWrite(channel)) throw new IOException("Data-channel acceptance bound exceeded.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); await _pump.ConfigureAwait(false);
        Task[] resets; lock (_gate) resets = _channels.Values.Select(channel => channel.ResetTask).OfType<Task>().ToArray();
        await Task.WhenAll(resets).ConfigureAwait(false); _lifetime.Dispose();
    }
}
