using Ariva.Core.Domain.Entities;
using Ariva.Core.Flights;

namespace Ariva.Core.Border;

/// <summary>
/// The e-gate reject coupling settings (<c>Border:EgateCoupling</c>, formulas F12): which manual lane receives the
/// rejects (a site rule, reference visitors; To confirm per site, one per deployment until per-site settings exist), the
/// walk from the gates (reference 1 minute), and the reject rate used until AMAN's e-gate statistics give one.
/// </summary>
public sealed record EgateCouplingSettings
{
    public const string SectionName = "Border:EgateCoupling";

    /// <summary>The manual lane category the rejected passengers join (CIT, RES, VIS or CRW).</summary>
    public string RejectLane { get; init; } = LaneCategory.Visitors;

    /// <summary>lag of F12: minutes from the gates to the manual queue.</summary>
    public int LagMinutes { get; init; } = 1;

    /// <summary>r when AMAN has not reported enough attempts in the window (reference 0.07).</summary>
    public double ReferenceRejectRate { get; init; } = 0.07;

    /// <summary>The window over which r is measured from AMAN's e-gate intervals.</summary>
    public int RateWindowMinutes { get; init; } = 30;

    /// <summary>Fewer attempts than this in the window and the reference rate is used.</summary>
    public int MinAttempts { get; init; } = 20;

    public IEnumerable<string> Problems()
    {
        if (RejectLane is null || !ImmigrationRules.Lanes.Contains(RejectLane))
            yield return $"{SectionName}:RejectLane is CIT, RES, VIS or CRW.";
        if (LagMinutes is < 0 or > 10)
            yield return $"{SectionName}:LagMinutes is 0 to 10.";
        if (!double.IsFinite(ReferenceRejectRate) || ReferenceRejectRate is < 0 or > 1)
            yield return $"{SectionName}:ReferenceRejectRate is 0 to 1.";
        if (RateWindowMinutes is < 5 or > 240)
            yield return $"{SectionName}:RateWindowMinutes is 5 to 240.";
        if (MinAttempts is < 1 or > 100_000)
            yield return $"{SectionName}:MinAttempts is 1 to 100,000.";
    }
}

/// <summary>
/// E-gate rejects as manual lane demand (formulas F12, ARV-049): <c>A_manual(t) = A_own(t) + r * F_gate(t - lag)</c>.
/// In the projection F_gate is the e-gate eligible arrivals (planning form); the minutes before the projection starts take
/// the rejects AMAN reported (live form). The e-gate lane keeps its arrivals: rejected passengers queue at the gates first.
/// Pure.
/// </summary>
public static class EgateCoupling
{
    /// <summary>r: rejects over attempts in the window when there are enough attempts, else the reference rate.</summary>
    public static (double Rate, bool Measured) RejectRate(long attempts, long rejected, EgateCouplingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (attempts < settings.MinAttempts || rejected < 0 || rejected > attempts)
            return (settings.ReferenceRejectRate, false);
        return ((double)rejected / attempts, true);
    }

    /// <summary>
    /// The minutes with the reject lane increased by <paramref name="rate"/> times the e-gate arrivals <paramref name="lag"/>
    /// minutes earlier; a minute whose source is before the first takes the rejects reported for it in
    /// <paramref name="liveRejects"/> (none otherwise).
    /// </summary>
    public static IReadOnlyList<MinuteDemand> Couple(IReadOnlyList<MinuteDemand> minutes, double rate, int lag, string rejectLane,
        IReadOnlyDictionary<DateTime, double> liveRejects = null)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        if (!double.IsFinite(rate) || rate is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(rate), "The reject rate is 0 to 1.");
        if (lag < 0)
            throw new ArgumentOutOfRangeException(nameof(lag), "The lag is 0 or more minutes.");
        var coupled = new List<MinuteDemand>(minutes.Count);
        for (var i = 0; i < minutes.Count; i++)
        {
            var source = i - lag;
            double rejects;
            if (source >= 0)
                rejects = rate * minutes[source].Lanes.EGate;
            else
                rejects = liveRejects is not null && liveRejects.TryGetValue(minutes[i].MinuteUtc.AddMinutes(-lag), out var live) && double.IsFinite(live) && live > 0 ? live : 0;
            coupled.Add(minutes[i] with { Lanes = minutes[i].Lanes.Add(rejectLane, rejects) });
        }

        return coupled;
    }
}
