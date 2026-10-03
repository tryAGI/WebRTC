using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace tryAGI.WebRTC;

public sealed record StunGatheringOptions
{
    public TimeSpan InitialRetransmissionTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public int MaximumRequests { get; init; } = 7;
    public int FinalWaitMultiplier { get; init; } = 16;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(40);
    /// <summary>Plain STUN permits an absent fingerprint. A present fingerprint is always validated.</summary>
    public bool RequireFingerprint { get; init; }
    internal void Validate()
    {
        if (InitialRetransmissionTimeout < TimeSpan.FromMilliseconds(100) || InitialRetransmissionTimeout > TimeSpan.FromSeconds(2) ||
            MaximumRequests is < 1 or > 10 || FinalWaitMultiplier is < 1 or > 32 ||
            Timeout < TimeSpan.FromMilliseconds(100) || Timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(StunGatheringOptions));
    }
}
public sealed record StunGatheringDiagnostics(int ActiveTransactions, long SentRequests, long Retransmissions,
    long RejectedResponses, long SuccessfulBindings);

public sealed partial class IceUdpTransport
{
    private readonly Dictionary<string, GatheringTransaction> _gathering = [];
    private long _gatheringSent, _gatheringRetries, _gatheringRejected, _gatheringSucceeded;

    public StunGatheringDiagnostics GetGatheringDiagnostics()
    { lock (_gate) return new(_gathering.Count, _gatheringSent, _gatheringRetries, _gatheringRejected, _gatheringSucceeded); }

    /// <summary>Unauthenticated STUN Binding on this owned socket. Does not authenticate a peer, resolve DNS or create a TURN allocation.</summary>
    public async Task<IceCandidate> GatherServerReflexiveCandidateAsync(IPEndPoint server, StunGatheringOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        var destination = new IceCandidate(server).EndPoint;
        var local = new IceCandidate(LocalEndPoint).EndPoint;
        if (destination.AddressFamily != local.AddressFamily) throw new ArgumentException("STUN server and socket families must match.", nameof(server));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lifetime.CancelAfter(options.Timeout); lifetime.Token.ThrowIfCancellationRequested();
        var transaction = new GatheringTransaction(destination, local, options.RequireFingerprint);
        lock (_gate)
        {
            ThrowIfStopped();
            if (_gathering.Count >= 8 || _gathering.Values.Any(t => t.Server.Equals(destination)))
                throw new InvalidOperationException("STUN admission allows at most eight servers and one transaction per server.");
            _gathering.Add(transaction.Id, transaction);
        }
        try
        {
            var packet = new byte[28];
            var writer = new StunMessageWriter(packet, StunMessage.BindingRequest, transaction.Bytes);
            if (!writer.TryComplete([], true, out var length)) throw new InvalidOperationException("STUN request framing failed.");
            for (var request = 0; request < options.MaximumRequests; request++)
            {
                if (transaction.Done.Task.IsCompleted) return await transaction.Done.Task.ConfigureAwait(false);
                await _socket.SendToAsync(packet.AsMemory(0, length), SocketFlags.None, destination, lifetime.Token).ConfigureAwait(false);
                lock (_gate) { _gatheringSent++; if (request != 0) _gatheringRetries++; }
                var multiplier = request == options.MaximumRequests - 1 ? options.FinalWaitMultiplier : 1 << request;
                var wait = TimeSpan.FromTicks(options.InitialRetransmissionTimeout.Ticks * multiplier);
                try { return await transaction.Done.Task.WaitAsync(wait, lifetime.Token).ConfigureAwait(false); }
                catch (TimeoutException) when (request < options.MaximumRequests - 1) { }
            }
            throw new TimeoutException("STUN Binding exhausted its bounded request budget.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("STUN gathering deadline expired."); }
        finally { lock (_gate) _gathering.Remove(transaction.Id); }
    }

    // Runs under the ICE gate, before its authenticated connectivity-check demux.
    private bool HandleGatheringResponse(ReadOnlySpan<byte> packet, IPEndPoint source)
    {
        if (_stopped || packet.Length > 2048 || !StunMessage.TryParse(packet, out var message) ||
            message.Type is not (BindingSuccess or BindingError) ||
            !_gathering.TryGetValue(Convert.ToHexString(message.TransactionId), out var transaction)) return false;
        if (transaction.Done.Task.IsCompleted) return true;
        var fingerprints = Count(message, StunMessage.Fingerprint);
        if (!source.Equals(transaction.Server) || ((fingerprints != 0 || transaction.RequireFingerprint) && !message.VerifyFingerprint()))
        { _gatheringRejected++; return true; }
        var attributes = message.GetAttributes();
        while (attributes.MoveNext())
        {
            // Known STUN attributes may be unexpected in an unauthenticated
            // Binding response; unknown comprehension-required attributes fail it.
            if (attributes.Type < 0x8000 && attributes.Type is not
                (0x0001 or 0x0006 or 0x0008 or 0x0009 or 0x000A or 0x0014 or 0x0015 or 0x001C or 0x001D or 0x001E or 0x0020))
            { transaction.Done.TrySetException(new InvalidDataException("STUN response has an unsupported required attribute.")); return true; }
        }
        if (message.Type == BindingError)
        {
            if (!message.TryGetUniqueAttribute(ErrorCode, out var code) || code.Length < 4 || code[0] != 0 || code[1] != 0 ||
                code[2] is < 3 or > 6 || code[3] > 99) { _gatheringRejected++; return true; }
            transaction.Done.TrySetException(new IOException($"STUN Binding failed with code {code[2] * 100 + code[3]}."));
            return true; // No implicit alternate-server redirect or credential retry.
        }
        if (!message.TryGetXorMappedEndpoint(out var mapped) || mapped!.AddressFamily != transaction.Local.AddressFamily)
        { _gatheringRejected++; return true; }
        try
        {
            var candidate = new IceCandidate(mapped, 1694498815, IceCandidateType.ServerReflexive, transaction.Local);
            if (transaction.Done.TrySetResult(candidate)) _gatheringSucceeded++;
        }
        catch (ArgumentException) { _gatheringRejected++; }
        return true;
    }
    private sealed class GatheringTransaction
    {
        public byte[] Bytes { get; } = RandomNumberGenerator.GetBytes(12);
        public string Id { get; }
        public IPEndPoint Server { get; }
        public IPEndPoint Local { get; }
        public bool RequireFingerprint { get; }
        public TaskCompletionSource<IceCandidate> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GatheringTransaction(IPEndPoint server, IPEndPoint local, bool requireFingerprint)
        { Id = Convert.ToHexString(Bytes); Server = server; Local = local; RequireFingerprint = requireFingerprint; }
    }
}
