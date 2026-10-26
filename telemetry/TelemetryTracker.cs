using Vzihk.Common.Reliability;

namespace Vzihk.Common.Telemetry;

public enum PongStatus { Accepted, Duplicate, Late, Unknown }

/// <summary>Bounded PING/PONG tracker sharing its RTT estimator with ACK handling.</summary>
public sealed class TelemetryTracker(AdaptiveTimeout timeout)
{
    private readonly Dictionary<ushort, long> pending = [];
    private readonly Dictionary<ushort, PongStatus> completed = [];
    private readonly Queue<ushort> history = [];
    private double? previousRtt;
    public double JitterMs { get; private set; }
    public double LastRttMs { get; private set; }
    public int AcceptedCount { get; private set; }
    public int LateCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int UnknownCount { get; private set; }
    public const long PingTimeoutUs = 1_000_000;

    public void OnPingSent(ushort sequence, long nowUs)
    {
        if (nowUs < 0) throw new ArgumentOutOfRangeException(nameof(nowUs));
        pending.Add(sequence, nowUs);
    }
    public PongStatus OnPong(ushort sequence, long nowUs)
    {
        if (pending.TryGetValue(sequence, out var sent))
        {
            if (nowUs < sent) throw new ArgumentOutOfRangeException(nameof(nowUs));
            pending.Remove(sequence);
            if (nowUs - sent >= PingTimeoutUs)
            {
                Remember(sequence, PongStatus.Late); LateCount++; return PongStatus.Late;
            }
            LastRttMs = (nowUs - sent) / 1000.0;
            if (previousRtt is double old) JitterMs += (Math.Abs(LastRttMs - old) - JitterMs) / 16;
            previousRtt = LastRttMs;
            timeout.OnSample(LastRttMs); AcceptedCount++;
            Remember(sequence, PongStatus.Duplicate); return PongStatus.Accepted;
        }
        var status = completed.GetValueOrDefault(sequence, PongStatus.Unknown);
        if (status == PongStatus.Duplicate) DuplicateCount++;
        else if (status == PongStatus.Late) LateCount++;
        else UnknownCount++;
        return status;
    }
    public void Expire(long nowUs)
    {
        foreach (var (sequence, sent) in pending.ToArray())
            if (nowUs - sent >= PingTimeoutUs) { pending.Remove(sequence); Remember(sequence, PongStatus.Late); }
    }
    private void Remember(ushort sequence, PongStatus status)
    {
        completed[sequence] = status; history.Enqueue(sequence);
        if (history.Count > 2048) completed.Remove(history.Dequeue());
    }
}
