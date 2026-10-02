namespace Ariva.Core.Sensing;

/// <summary>
/// The clock offset of one device (F19, ARV-023), estimated from the device's own send time against Ariva's receive time
/// on every push: an exponentially weighted moving average (weight <see cref="Alpha"/>) of the raw offsets, plus the
/// last <see cref="Window"/> raw readings for the stability test. The raw offset also carries the network delay, a few
/// milliseconds on a site LAN, which is far below the 500 ms tolerance. Pure and immutable: <see cref="Next"/> returns
/// the next estimate.
/// </summary>
public sealed record ClockOffset(double EstimateMilliseconds, IReadOnlyList<double> Recent)
{
    /// <summary>Weight of the newest reading: a step change is mostly absorbed within about ten pushes.</summary>
    public const double Alpha = 0.2;

    /// <summary>Readings kept for the stability test (F19: the last 10).</summary>
    public const int Window = 10;

    /// <summary>F19: offsets beyond this are corrected or make the clock unreliable.</summary>
    public const double ToleranceMilliseconds = 500;

    /// <summary>F19 (Proposed): stable when the readings' standard deviation is under this.</summary>
    public const double StableDeviationMilliseconds = 50;

    public static ClockOffset None { get; } = new(0, []);

    /// <summary>The estimate after one more reading: <paramref name="deviceUtc"/> as the device stamped it, <paramref name="receivedUtc"/> by Ariva.</summary>
    public ClockOffset Next(DateTime deviceUtc, DateTime receivedUtc)
    {
        var raw = (deviceUtc - receivedUtc).TotalMilliseconds;
        // A reading beyond a day is not an offset but a broken clock or a forged time: it does not move the estimate.
        if (!double.IsFinite(raw) || Math.Abs(raw) > CanonicalEventRules.MaxClockOffsetMilliseconds)
            return this;
        var estimate = Recent.Count == 0 ? raw : (Alpha * raw) + ((1 - Alpha) * EstimateMilliseconds);
        return new ClockOffset(estimate, [.. Recent.Skip(Math.Max(0, Recent.Count - (Window - 1))), raw]);
    }

    /// <summary>F19 stability: at least three readings and their standard deviation under 50 ms.</summary>
    public bool Stable
    {
        get
        {
            if (Recent.Count < 3)
                return false;
            var mean = Recent.Average();
            return Math.Sqrt(Recent.Sum(r => (r - mean) * (r - mean)) / Recent.Count) < StableDeviationMilliseconds;
        }
    }

    /// <summary>
    /// Ok while the estimate and every recent reading are within the tolerance; beyond it, Corrected when stable and
    /// Unreliable otherwise (F19 tests: 620, 610, 630 ms is corrected; 100, 900, 300 ms is unreliable).
    /// </summary>
    public ClockState State =>
        Math.Abs(EstimateMilliseconds) <= ToleranceMilliseconds && Recent.All(r => Math.Abs(r) <= ToleranceMilliseconds)
            ? ClockState.Ok
            : Stable ? ClockState.Corrected : ClockState.Unreliable;

    /// <summary>
    /// The offset used to correct: the mean of the recent readings when they are stable (what the stability test looked
    /// at, and not lagging like the moving average after a step), the moving average otherwise.
    /// </summary>
    public double CorrectionMilliseconds => Stable ? Recent.Average() : EstimateMilliseconds;

    public ClockReading Reading => new(Math.Round(State == ClockState.Corrected ? CorrectionMilliseconds : EstimateMilliseconds, 1), Stable, State);

    /// <summary>An event time in Ariva's clock: corrected when the clock is off but stable; never moved out of range.</summary>
    public DateTime Correct(DateTime deviceUtc)
    {
        if (State != ClockState.Corrected)
            return deviceUtc;
        var ticks = deviceUtc.Ticks - (long)(CorrectionMilliseconds * TimeSpan.TicksPerMillisecond);
        return ticks <= DateTime.MinValue.Ticks || ticks >= DateTime.MaxValue.Ticks ? deviceUtc : new DateTime(ticks, DateTimeKind.Utc);
    }
}
