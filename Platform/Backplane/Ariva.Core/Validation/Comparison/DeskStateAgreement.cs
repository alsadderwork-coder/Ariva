using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// Desk-state agreement (formulas F18, ARV-104f): an observer's log of border desk states, minute by minute (ARV-104b), against
/// the desk's stored minute (<c>desk_minute</c>, F10). agreement = observed minutes whose dominant stored state equals the
/// observed state / observed minutes. Rules Proposed for the owner (docs/product/decisions.md): the dominant state of a stored
/// minute is the state with the most seconds, its unaccounted seconds counting as Unknown, a tie with Unknown going to
/// Unknown and a tie between known states to the more active (Serving, Idle, Paused, Closed, as the live desk screen shows
/// it); Unknown counts as disagreement, and so does an observed minute without a usable stored minute; each observer's highest
/// revision of a desk and minute is its state (a correction is a revision); several observers of one desk and minute are one
/// observed minute, judged only when every one's state is the same (otherwise excluded, counted apart as the ground truth's
/// own disagreement; the exclusion lever this opens, and the strict agreement that closes it, are on
/// <see cref="DeskAgreementSummary"/>); a desk minute an observer's unusable state names is not judged, and the other
/// observers' states of it are left out with it (security review lesson of ARV-104e, CWE-501). Degraded stored minutes count,
/// tallied apart. Border per-desk data throughout: no person is named, and ARV-104g serves the results to border roles only.
/// Pure: no I/O, no clock.
/// </summary>
public static class DeskStateAgreement
{
    #region Formulas

    /// <summary>The seconds of a minute.</summary>
    public const double MinuteSeconds = 60;

    /// <summary>
    /// The dominant state of a stored desk minute (Proposed): the state with the most seconds, the seconds the minute does not
    /// account for (its states summing to less than 60) counting as Unknown; a tie with Unknown is Unknown (fail closed: the
    /// system did not know the state for as long as it knew any), a tie between known states goes to the more active one
    /// (Serving, Idle, Paused, Closed), as the live desk screen breaks it. Seconds within 1e-9 of each other tie. Null when the
    /// seconds cannot be: not a number, below 0, above 60, or more than a minute in all (beyond a microsecond of rounding).
    /// </summary>
    public static DeskStatus? Dominant(double closed, double idle, double serving, double paused, double unknown)
    {
        double[] seconds = [closed, idle, serving, paused, unknown];
        if (!seconds.All(s => double.IsFinite(s) && s >= 0 && s <= MinuteSeconds))
            return null;
        var total = closed + idle + serving + paused + unknown;
        if (Math.Round(total - MinuteSeconds, 6) > 0)
            return null;

        (DeskStatus State, double Seconds)[] candidates =
        [
            (DeskStatus.Unknown, unknown + Math.Max(0, MinuteSeconds - total)), (DeskStatus.Serving, serving), (DeskStatus.Idle, idle),
            (DeskStatus.Paused, paused), (DeskStatus.Closed, closed)
        ];
        var best = candidates[0];
        foreach (var candidate in candidates)
        {
            if (Math.Round(candidate.Seconds - best.Seconds, 9) > 0)
                best = candidate;
        }

        return best.State;
    }

    /// <summary>The stored state an observed state names (the four states a person can see; never Unknown).</summary>
    public static DeskStatus StatusOf(ObservedDeskState observed) =>
        observed switch
        {
            ObservedDeskState.Closed => DeskStatus.Closed,
            ObservedDeskState.Idle => DeskStatus.Idle,
            ObservedDeskState.Serving => DeskStatus.Serving,
            ObservedDeskState.Paused => DeskStatus.Paused,
            _ => DeskStatus.Unknown
        };

    /// <summary>Whether an observed state equals the stored dominant state; Unknown never agrees (F18, Proposed).</summary>
    public static bool Agrees(ObservedDeskState observed, DeskStatus system) => system != DeskStatus.Unknown && StatusOf(observed) == system;

    /// <summary>Whether a state counts for throughput (F8's n_open: Idle or Serving).</summary>
    public static bool CountsForThroughput(DeskStatus status) => status is DeskStatus.Idle or DeskStatus.Serving;

    /// <summary>Agreeing minutes over judged minutes; null with none judged or counts that cannot be (below 0, more agreeing than judged).</summary>
    public static double? Agreement(int agreeing, int judged) => judged > 0 && agreeing >= 0 && agreeing <= judged ? (double)agreeing / judged : null;

    #endregion

    #region Comparison

    /// <summary>
    /// Every observed desk minute (by checkpoint, desk code and minute), each desk's summary (every desk in scope, with or
    /// without observations, by codes) and the summary over every desk.
    /// </summary>
    internal static (IReadOnlyList<DeskMinuteAgreement> Minutes, IReadOnlyList<DeskAgreementSummary> Desks, DeskAgreementSummary Overall) Compare(ComparisonData data)
    {
        var items = new List<DeskMinuteAgreement>();
        var observed = data.DeskObservations.GroupBy(o => (o.DeskId, o.MinuteUtc))
            .Select(g => (Desk: data.DeskById[g.Key.DeskId], Minute: new DateTime(g.Key.MinuteUtc.Ticks, DateTimeKind.Utc), States: g.Select(o => o.State).ToList()))
            .OrderBy(g => g.Desk.CheckpointCode, StringComparer.Ordinal).ThenBy(g => g.Desk.DeskCode, StringComparer.Ordinal).ThenBy(g => g.Minute);
        foreach (var (desk, minute, states) in observed)
        {
            var row = data.DeskMinuteAt(desk, minute);
            // A usable row always has a dominant state (its seconds were checked); none, or unusable rows, is Unknown.
            var system = row is null ? DeskStatus.Unknown
                : Dominant(row.ClosedSeconds, row.IdleSeconds, row.ServingSeconds, row.PausedSeconds, row.UnknownSeconds) ?? DeskStatus.Unknown;
            var standing = system == DeskStatus.Unknown ? ComparisonStanding.Unknown
                : row.Degraded || row.UnknownSeconds > 0 || !IsWholeMinute(row) ? ComparisonStanding.Degraded
                : ComparisonStanding.Good;
            var disagree = states.Distinct().Count() > 1;
            ObservedDeskState? state = disagree ? null : states[0];
            items.Add(new DeskMinuteAgreement(data.Version, desk.DeskId, desk.CheckpointCode, desk.DeskCode, minute, states.Count, state, disagree, system,
                row is not null, standing, state is { } s ? Agrees(s, system) : null));
        }

        var byDesk = items.GroupBy(i => i.DeskId).ToDictionary(g => g.Key, g => g.ToList());
        var desks = data.Desks.Select(d => Summarise(data, d, byDesk.GetValueOrDefault(d.DeskId) ?? [], data.UnusableObservationMinutes.GetValueOrDefault(d.DeskId)))
            .ToList();
        return (items.AsReadOnly(), desks.AsReadOnly(), Summarise(data, null, items, data.UnusableObservationMinutes.Values.Sum()));
    }

    /// <summary>Whether a stored minute accounts for its whole 60 seconds (to a microsecond); a part not accounted for is unknown time.</summary>
    private static bool IsWholeMinute(DeskMinuteRow row) =>
        Math.Round(MinuteSeconds - (row.ClosedSeconds + row.IdleSeconds + row.ServingSeconds + row.PausedSeconds + row.UnknownSeconds), 6) <= 0;

    private static DeskAgreementSummary Summarise(ComparisonData data, ScopeDesk desk, List<DeskMinuteAgreement> items, int unusable)
    {
        var target = data.Settings.DeskAgreementTarget;
        var judged = items.Where(i => !i.ObserversDisagree).ToList();
        var agreeing = judged.Count(i => i.Agrees == true);
        var agreement = Agreement(agreeing, judged.Count);
        var throughput = Agreement(judged.Count(i => CountsForThroughput(StatusOf(i.ObservedState.GetValueOrDefault())) == CountsForThroughput(i.SystemState) &&
                                                   i.SystemState != DeskStatus.Unknown), judged.Count);
        var cells = judged.GroupBy(i => (i.ObservedState.GetValueOrDefault(), i.SystemState)).ToDictionary(g => g.Key, g => g.Count());
        var confusion = Enum.GetValues<ObservedDeskState>()
            .SelectMany(o => Enum.GetValues<DeskStatus>().Select(s => new DeskStateCount(o, s, cells.GetValueOrDefault((o, s)))))
            .ToList();
        var verdict = agreement is { } a
            ? CountAccuracy.AtLeast(a, target) ? CriterionVerdict.Pass : CriterionVerdict.Fail
            : CriterionVerdict.NoData;
        var disagree = items.Count - judged.Count;
        // Fail closed beside the Proposed rule: every excluded minute counted as a disagreement (the exclusion lever, ARV-104g).
        var strict = Agreement(agreeing, judged.Count + disagree + unusable);
        return new DeskAgreementSummary(data.Version, desk?.DeskId, desk?.CheckpointCode, desk?.DeskCode, items.Count, judged.Count, agreeing, disagree, unusable,
            disagree + unusable, judged.Count(i => !i.SystemStored), StandingTally.Of(judged.Select(i => i.Standing)), throughput, strict, confusion.AsReadOnly(),
            new CriterionCheck(agreement, target, verdict));
    }

    #endregion
}
