namespace Ariva.Core.Queueing;

/// <summary>
/// The desk term of the nowcast (F8) for one queue at a minute: n_open, the desks Idle or Serving, and c, the mean cycle
/// time per desk in minutes (null when no transaction was seen in the window). <see cref="Degraded"/> when a desk's state
/// was Unknown or degraded, or a desk's minutes stopped before the others'.
/// </summary>
public sealed record DeskTerm(DateTime AsOfMinuteUtc, int OpenServers, double? CycleMinutes, bool Degraded, int Desks)
{
    /// <summary>
    /// The same desks' term without any AMAN input (ARV-117, <see cref="DeskTerms.SensorOnly"/>), for the shadow nowcast
    /// only; null when the sensors say nothing about how many desks are open.
    /// </summary>
    public DeskTerm SensorOnly { get; init; }
}

/// <summary>
/// One desk's closed minute as the desk state engine wrote it (<c>desk_minute</c>, F10). <see cref="SensorDerivedSeconds"/>
/// (ARV-116) is the part of its Idle and Serving time whose state came from its staff and service zones alone.
/// </summary>
public sealed record DeskMinuteSample(string DeskKey, DateTime MinuteUtc, double IdleSeconds, double ServingSeconds, double UnknownSeconds, int Transactions, bool Degraded,
    double SensorDerivedSeconds = 0)
{
    /// <summary>tau (F9): the seconds the desk was open for throughput.</summary>
    public double OpenSeconds => Math.Max(0, IdleSeconds) + Math.Max(0, ServingSeconds);

    /// <summary>The open seconds the desk's zones alone proved (never more than <see cref="OpenSeconds"/>).</summary>
    public double SensorOpenSeconds => double.IsFinite(SensorDerivedSeconds) ? Math.Clamp(SensorDerivedSeconds, 0, OpenSeconds) : 0;
}

/// <summary>
/// Builds a queue's <see cref="DeskTerm"/> from the closed minutes of the desks serving it (ARV-064). The latest minute
/// any of them has closed is the term's minute: a desk is open there when it was Idle or Serving for at least half of it
/// (n_open), and Unknown for half of it, or degraded, flags the term; with no desk open and any Unknown there is no term
/// (n_open is not known, which is not "nothing open"). c is the lane cycle time from the border system's interval
/// statistics when given (F10, <see cref="Ariva.Core.Desks.LaneCycle"/>); otherwise the open time per transaction over the
/// last <c>windowMinutes</c> minutes up to that minute, across the desks (when queues are long, desks are serving nearly
/// all of their open time, so this is the cycle time; with short queues it is longer, which only lowers the desk term).
/// Pure.
/// </summary>
public static class DeskTerms
{
    private const double HalfMinuteSeconds = 30;

    public static DeskTerm Compute(IEnumerable<DeskMinuteSample> minutes, int windowMinutes, DateTime notAfterUtc, double? laneCycleMinutes = null)
    {
        var published = Published(minutes, windowMinutes, notAfterUtc, laneCycleMinutes);
        return published is null ? null : published with { SensorOnly = SensorOnly(minutes, windowMinutes, notAfterUtc) };
    }

    private static DeskTerm Published(IEnumerable<DeskMinuteSample> minutes, int windowMinutes, DateTime notAfterUtc, double? laneCycleMinutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        if (windowMinutes is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(windowMinutes), "The window is from 1 to 60 minutes.");
        var usable = minutes.Where(m => m is not null && m.DeskKey is not null && m.MinuteUtc <= notAfterUtc &&
                                        double.IsFinite(m.IdleSeconds) && double.IsFinite(m.ServingSeconds) && double.IsFinite(m.UnknownSeconds)).ToList();
        if (usable.Count == 0)
            return null;

        var asOf = usable.Max(m => m.MinuteUtc);
        var latest = usable.GroupBy(m => m.DeskKey, StringComparer.Ordinal).Select(g => System.Linq.Enumerable.MaxBy(g, m => m.MinuteUtc)).ToList();
        var current = latest.Where(m => m.MinuteUtc == asOf).ToList();
        var open = current.Count(m => m.OpenSeconds >= HalfMinuteSeconds);
        // A desk whose minutes stopped before the others' (its feed is behind) is not counted, and the term says so.
        var unknown = current.Any(m => m.Degraded || m.UnknownSeconds >= HalfMinuteSeconds);
        var degraded = current.Count < latest.Count || unknown;
        // "Nothing open" needs every desk known to be closed: with none open and any Unknown, n_open is not known.
        if (open == 0 && unknown)
            return null;

        var from = asOf.AddMinutes(-windowMinutes);
        var window = usable.Where(m => m.MinuteUtc > from).ToList();
        var transactions = window.Sum(m => (long)Math.Max(0, m.Transactions));
        var openMinutes = window.Sum(m => m.OpenSeconds) / 60;
        // The lane cycle time from AMAN's own interval statistics (F10, LaneCycle) when there is one; otherwise open time per
        // transaction from the desk minutes.
        double? cycle = laneCycleMinutes is { } lane && double.IsFinite(lane) && lane > 0
            ? lane
            : transactions > 0 && openMinutes > 0 ? openMinutes / transactions : null;
        return new DeskTerm(asOf, open, cycle, degraded, latest.Count);
    }

    /// <summary>
    /// The sensor-only desk term of the shadow nowcast (ARV-117, F8): the term <see cref="Compute"/> gives with no AMAN
    /// input. n_open counts the desks open for at least half of the term's minute by their staff and service zones alone
    /// (sensor-derived, F10 rows 6 and 7); a desk open only through AMAN (a session, a recent transaction) is not seen by
    /// the sensors, so it is treated as an Unknown desk is: it flags the term, and with no desk open from the sensors there
    /// is no term (n_open is not known, which is not "nothing open"). c is null: AMAN's interval statistics and the
    /// transactions the desk minutes count both come from AMAN, and the sensors give no service starts, so the nowcast
    /// falls back as F8 does without a cycle time (the exit term alone, or no service when nothing is open; Proposed,
    /// docs/product/decisions.md). Where every open desk minute is sensor-derived and no AMAN statistics exist (a site
    /// without AMAN), the sensor-only term equals the published one. Pure.
    /// </summary>
    public static DeskTerm SensorOnly(IEnumerable<DeskMinuteSample> minutes, int windowMinutes, DateTime notAfterUtc)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        if (windowMinutes is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(windowMinutes), "The window is from 1 to 60 minutes.");
        var usable = minutes.Where(m => m is not null && m.DeskKey is not null && m.MinuteUtc <= notAfterUtc &&
                                        double.IsFinite(m.IdleSeconds) && double.IsFinite(m.ServingSeconds) && double.IsFinite(m.UnknownSeconds)).ToList();
        if (usable.Count == 0)
            return null;

        var asOf = usable.Max(m => m.MinuteUtc);
        var latest = usable.GroupBy(m => m.DeskKey, StringComparer.Ordinal).Select(g => System.Linq.Enumerable.MaxBy(g, m => m.MinuteUtc)).ToList();
        var current = latest.Where(m => m.MinuteUtc == asOf).ToList();
        var open = current.Count(m => m.SensorOpenSeconds >= HalfMinuteSeconds);
        // A desk open through AMAN only is a desk the sensors cannot see: Unknown to the shadow.
        var unseen = current.Any(m => m.OpenSeconds >= HalfMinuteSeconds && m.SensorOpenSeconds < HalfMinuteSeconds);
        var unknown = unseen || current.Any(m => m.Degraded || m.UnknownSeconds >= HalfMinuteSeconds);
        var degraded = current.Count < latest.Count || unknown;
        if (open == 0 && unknown)
            return null;
        return new DeskTerm(asOf, open, null, degraded, latest.Count);
    }
}
