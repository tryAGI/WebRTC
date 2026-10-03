using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public sealed record DataChannelLimits
{
    public int MaximumChannels { get; init; } = 64;
    public int MaximumQueuedMessages { get; init; } = 128;
    public int ReceiveBufferBytes { get; init; } = 1024 * 1024;
}

/// <summary>Reliable ordered/unordered DCEP channels. Owns the SCTP message reader, not the association lifetime.</summary>
public sealed class DataChannelAssociation : IAsyncDisposable
{
    private readonly SctpAssociation _sctp;
    private readonly DataChannelLimits _limits;
    private readonly object _gate = new();
    private readonly Dictionary<ushort, DataChannel> _channels = [];
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _sctp.SendMessageAsync(channel.StreamId, 50, open, cancellationToken: linked.Token).ConfigureAwait(false);
            await channel.Opened.WaitAsync(linked.Token).ConfigureAwait(false); return channel;
        }
        catch (Exception error) { channel.End(error); throw; } // Retire the ID; reuse requires stream reset.
    }
    public IAsyncEnumerable<DataChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default) => _accepted.Reader.ReadAllAsync(cancellationToken);
    internal async ValueTask SendAsync(DataChannel channel, uint ppid, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        lock (_gate) RequireRunning();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _sctp.SendMessageAsync(channel.StreamId, ppid, bytes, !channel.Parameters.Ordered, linked.Token,
            new(channel.Parameters.Reliability, channel.Parameters.ReliabilityParameter)).ConfigureAwait(false);
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
            await foreach (var message in _sctp.ReceiveMessagesAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (message.PayloadProtocolIdentifier == 50) { await ProcessDcepAsync(message).ConfigureAwait(false); continue; }
                DataChannel channel;
                lock (_gate)
                {
                    if (!_channels.TryGetValue(message.StreamId, out channel!) || !channel.IsOpen)
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
                        if (_queuedMessages < _limits.MaximumQueuedMessages && _queuedBytes + bytes.Length <= _limits.ReceiveBufferBytes)
                        { _queuedMessages++; _queuedBytes += bytes.Length; channel.Deliver(new(text ? DataChannelMessageKind.Text : DataChannelMessageKind.Binary, bytes)); break; }
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
                foreach (var channel in _channels.Values) channel.End(reason);
                _accepted.Writer.TryComplete(reason); _space.TrySetException(reason ?? new IOException("Data channels closed."));
                _completion.TrySetResult(reason);
            }
        }
    }
    private async Task ProcessDcepAsync(SctpMessage message)
    {
        if (message.Unordered) throw new IOException("DCEP requires ordered reliable delivery.");
        if (DataChannelProtocol.IsAcknowledgment(message.Data))
        {
            lock (_gate)
            { if (!_channels.TryGetValue(message.StreamId, out var existing)) throw new IOException("DCEP ACK without OPEN."); existing.Acknowledge(); }
            return;
        }
        if (!DataChannelProtocol.TryParseOpen(message.Data, out var parameters)) throw new IOException("Malformed DCEP OPEN.");
        if (parameters!.Reliability != DataChannelReliability.Reliable && !_sctp.SupportsPartialReliability)
            throw new IOException("Partial reliability was not negotiated.");
        var localParity = _sctp.DtlsRole == DtlsRole.Client ? 0 : 1;
        if ((message.StreamId & 1) == localParity || message.StreamId >= _sctp.OutgoingStreams)
            throw new IOException("Invalid DCEP stream parity or bounds.");
        DataChannel channel;
        lock (_gate)
        {
            if (_channels.Count >= _limits.MaximumChannels || _channels.ContainsKey(message.StreamId)) throw new IOException("DCEP channel bound or duplicate OPEN.");
            channel = new(this, message.StreamId, parameters, _limits.MaximumQueuedMessages); _channels.Add(message.StreamId, channel);
        }
        await _sctp.SendMessageAsync(message.StreamId, 50, new byte[] { 2 }, cancellationToken: _lifetime.Token).ConfigureAwait(false);
        channel.Acknowledge();
        if (!_accepted.Writer.TryWrite(channel)) throw new IOException("Data-channel acceptance bound exceeded.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); await _pump.ConfigureAwait(false); _lifetime.Dispose();
    }
}
