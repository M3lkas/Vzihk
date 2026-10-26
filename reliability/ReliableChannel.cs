namespace Vzihk.Common.Reliability;

public sealed record PendingPacket(ushort Sequence, ReadOnlyMemory<byte> RawBytes,
    long FirstSentAtUs, long LastSentAtUs, int Attempts);
public sealed record DeliveryReceipt(ushort Sequence, int Attempts, long FirstSentAtUs, long CompletedAtUs)
{
    public double TimeToAckMs => (CompletedAtUs - FirstSentAtUs) / 1000.0;
    // Karn: an ACK after retransmission has an ambiguous RTT, so skip that sample.
    public double? RttSampleMs => Attempts == 1 ? TimeToAckMs : null;
}
public sealed record DeliveryFailure(ushort Sequence, int Attempts, long FirstSentAtUs, long FailedAtUs);

/// <summary>Single-owner, deterministic delivery bookkeeping. No sockets, timers or wall clock.</summary>
public sealed class ReliableChannel
{
    private readonly Dictionary<ushort, PendingPacket> pending = [];
    private readonly List<DeliveryFailure> failed = [];
    public int MaxAttempts { get; }
    public int PendingCount => pending.Count;
    public int FailedCount => failed.Count;
    public IReadOnlyList<DeliveryFailure> Failed => failed.AsReadOnly();

    public ReliableChannel(int maxAttempts = 5)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        MaxAttempts = maxAttempts;
    }
    public void OnSent(ushort sequence, ReadOnlySpan<byte> rawBytes, long nowUs)
    {
        if (nowUs < 0) throw new ArgumentOutOfRangeException(nameof(nowUs));
        if (!pending.TryAdd(sequence, new(sequence, rawBytes.ToArray(), nowUs, nowUs, 1)))
            throw new InvalidOperationException("Sequence уже ожидает подтверждения");
    }
    public bool OnAckReceived(ushort sequence, long nowUs, out DeliveryReceipt? receipt)
    {
        receipt = null;
        if (!pending.TryGetValue(sequence, out var packet)) return false;
        if (nowUs < packet.LastSentAtUs) throw new ArgumentOutOfRangeException(nameof(nowUs));
        pending.Remove(sequence);
        receipt = new(sequence, packet.Attempts, packet.FirstSentAtUs, nowUs);
        return true;
    }
    public IReadOnlyList<PendingPacket> CollectForRetransmission(long nowUs, long adaptiveTimeoutUs)
    {
        if (adaptiveTimeoutUs <= 0) throw new ArgumentOutOfRangeException(nameof(adaptiveTimeoutUs));
        if (nowUs < 0) throw new ArgumentOutOfRangeException(nameof(nowUs));
        var due = new List<PendingPacket>();
        foreach (var (sequence, packet) in pending.ToArray())
        {
            if (nowUs < packet.LastSentAtUs) throw new ArgumentOutOfRangeException(nameof(nowUs));
            if (nowUs - packet.LastSentAtUs < adaptiveTimeoutUs) continue;
            if (packet.Attempts >= MaxAttempts)
            {
                pending.Remove(sequence);
                failed.Add(new(sequence, packet.Attempts, packet.FirstSentAtUs, nowUs));
            }
            else
            {
                var retry = packet with { Attempts = packet.Attempts + 1, LastSentAtUs = nowUs };
                pending[sequence] = retry; due.Add(retry);
            }
        }
        return due;
    }
}
