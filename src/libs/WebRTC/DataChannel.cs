using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace tryAGI.WebRTC;

public enum DataChannelMessageKind { Text, Binary }
public sealed record DataChannelMessage(DataChannelMessageKind Kind, byte[] Data)
{
    public string GetText() => Kind == DataChannelMessageKind.Text ? DataChannelProtocol.Utf8.GetString(Data) : throw new InvalidOperationException("Binary message.");
}
public sealed class DataChannel : IAsyncDisposable
{
    private readonly DataChannelAssociation _owner;
    private readonly Channel<DataChannelMessage> _incoming;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed;
    internal bool IncomingReset, OutgoingReset, DiscardIncoming;
    internal Exception? OpeningFailure;
    internal Task? ResetTask;
    internal bool CanReceive => _opened.Task.IsCompletedSuccessfully && !IncomingReset && Volatile.Read(ref _closed) != 2;
    public Task<Exception?> Completion => _completion.Task;
    public ushort StreamId { get; }
    public DataChannelParameters Parameters { get; }
    public bool IsOpen => _opened.Task.IsCompletedSuccessfully && Volatile.Read(ref _closed) == 0;
    internal Task Opened => _opened.Task;
    internal DataChannel(DataChannelAssociation owner, ushort streamId, DataChannelParameters parameters, int limit)
    {
        _owner = owner; StreamId = streamId; Parameters = parameters;
        _incoming = Channel.CreateBounded<DataChannelMessage>(new BoundedChannelOptions(limit)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true });
    }
    public ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        if (DataChannelProtocol.Utf8.GetByteCount(text) > _owner.MaximumMessageSize) throw new ArgumentOutOfRangeException(nameof(text));
        var bytes = DataChannelProtocol.Utf8.GetBytes(text);
        return _owner.SendAsync(this, bytes.Length == 0 ? 56u : 51u, bytes.Length == 0 ? new byte[] { 0 } : bytes, cancellationToken);
    }
    public ValueTask SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return _owner.SendAsync(this, bytes.Length == 0 ? 57u : 53u, bytes.Length == 0 ? new byte[] { 0 } : bytes, cancellationToken);
    }
    public async IAsyncEnumerable<DataChannelMessage> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        { _owner.ReleaseMessage(message.Data.Length); yield return message; }
    }
    public Task CloseAsync(CancellationToken cancellationToken = default) => _owner.CloseChannelAsync(this, cancellationToken);
    /// <summary>Discards unread application messages and initiates closure; use CloseAsync to wait for the wire exchange.</summary>
    public ValueTask DisposeAsync() { _owner.DiscardChannel(this); return ValueTask.CompletedTask; }
    internal void BeginClosing() => Interlocked.CompareExchange(ref _closed, 1, 0);
    internal int DiscardBuffered(out int bytes)
    {
        DiscardIncoming = true; BeginClosing(); _incoming.Writer.TryComplete(); bytes = 0; var count = 0;
        while (_incoming.Reader.TryRead(out var message)) { bytes += message.Data.Length; count++; }
        return count;
    }
    private void RequireOpen() { if (!IsOpen) throw new InvalidOperationException("Data channel is not open."); }
    internal void Acknowledge() { if (Volatile.Read(ref _closed) != 2) _opened.TrySetResult(); }
    internal void Deliver(DataChannelMessage message)
    { if (!_incoming.Writer.TryWrite(message)) throw new IOException("Data channel receive accounting invariant failed."); }
    internal void End(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 2) == 2) return;
        _opened.TrySetException(error ?? new IOException("Data channel closed.")); _incoming.Writer.TryComplete(error); _completion.TrySetResult(error);
    }
}
