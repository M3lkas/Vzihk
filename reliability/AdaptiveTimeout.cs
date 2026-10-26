namespace Vzihk.Common.Reliability;

/// <summary>Jacobson estimator; the assignment uses game-specific 100..3000 ms bounds.</summary>
public sealed class AdaptiveTimeout
{
    public double SrttMs { get; private set; }
    public double RttVarMs { get; private set; }
    public bool Initialized { get; private set; }
    public double RtoMs => Initialized ? Math.Clamp(SrttMs + 4 * RttVarMs, 100, 3000) : 1000;
    public long RtoUs => (long)Math.Ceiling(RtoMs * 1000);

    public void OnSample(double rttMs)
    {
        if (!double.IsFinite(rttMs) || rttMs < 0) throw new ArgumentOutOfRangeException(nameof(rttMs));
        if (!Initialized)
        {
            SrttMs = rttMs; RttVarMs = rttMs / 2; Initialized = true;
        }
        else
        {
            // Use the previous SRTT when updating variance (RFC 6298, 2.3).
            RttVarMs = 0.75 * RttVarMs + 0.25 * Math.Abs(SrttMs - rttMs);
            SrttMs = 0.875 * SrttMs + 0.125 * rttMs;
        }
    }
}
