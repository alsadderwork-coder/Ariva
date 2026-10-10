using Ariva.Core.Availability;
using Ariva.Core.Validation.Comparison;

namespace Ariva.Core.Validation;

/// <summary>The pilot criteria a campaign is judged on (formulas F18, D6). The ground-truth proof has no target and no verdict.</summary>
public enum CampaignCriterion
{
    CountAccuracy,
    WaitError,
    WaitBias,
    TrackCompletion,
    DeskStateAgreement,
    NowcastError,
    Availability
}

/// <summary>
/// How a campaign verdict treats the items an exclusion lever takes out of a criterion (security review of ARV-104f, Medium):
/// desk minutes whose observers disagree or whose observer state cannot be used, and minutes whose published nowcast gave no
/// number while the next minute's entrants waited under the cut.
/// </summary>
public enum ExclusionRule
{
    /// <summary>Proposed default: every excluded item counts against the system (a disagreement, an error beyond the target).</summary>
    Strict,

    /// <summary>The engine's figure over judged items, but the criterion fails when the excluded share exceeds a cap.</summary>
    Capped
}

/// <summary>Why a criterion's verdict is what it is.</summary>
public enum CampaignVerdictReason
{
    /// <summary>Every unit had its target count of judged items and met its target.</summary>
    Met,

    /// <summary>A unit with its target count of judged items missed its target.</summary>
    NotMet,

    /// <summary>A unit had fewer judged items than the campaign's target count (no data, whatever the items judged showed).</summary>
    TooFewJudged,

    /// <summary>
    /// The campaign holds nothing this criterion applies to: no desks; for track completion, no zone counted tracks (not tracked,
    /// or every tracker silent), always shown next to the review flag <see cref="CampaignReview.ZonesWithoutTracks"/> (wording of
    /// the ARV-104g2 re-check, ARV-104g).
    /// </summary>
    NotInScope,

    /// <summary>With <see cref="ExclusionRule.Capped"/>: the excluded share is above the cap (a fail).</summary>
    ExcludedShareAboveCap,

    /// <summary>The comparison compared nothing (its inputs were out of bounds or its scope invalid).</summary>
    NotCompared,

    /// <summary>Some zones counted tracks and another counted none all campaign: no data rather than a pass on the others (M2).</summary>
    ZonesWithoutTracks
}

/// <summary>What a campaign's results need a person to look at before they are relied on.</summary>
public enum CampaignReview
{
    /// <summary>
    /// The engine left out quality intervals (wholly before 2000, of a zone out of scope, or that could not be placed): such an
    /// interval is only counted and fails open, so the bins it should have degraded may stand Good (ARV-104e re-check).
    /// </summary>
    QualityIntervalsLeftOut,

    /// <summary>Stored rows a criterion needed were refused or conflicting (their keys are listed); no earlier revision stood in.</summary>
    UnusableRows,

    /// <summary>The targets of judged items are the placeholder defaults, not agreed for this campaign (TC-04).</summary>
    PlaceholderTargets,

    /// <summary>The campaign is not closed: more ground truth and later revisions of the stored results may still arrive.</summary>
    CampaignNotClosed,

    /// <summary>Some compared items wait for stored results that are not final yet (Provisional).</summary>
    ProvisionalResults,

    /// <summary>The nowcast without AMAN inputs was not read (no validation reader login here): the ground-truth proof is missing.</summary>
    ProofNotRead,

    /// <summary>
    /// A queue zone in scope counted no track all campaign: its sensors may not track people, or its tracker produced nothing
    /// (the zones are listed among the track completion's units; security review of ARV-104g2, M2).
    /// </summary>
    ZonesWithoutTracks,

    /// <summary>
    /// Planned minutes after which people's waits are known had no published nowcast at all (no row, no live part, or a row that
    /// cannot be used; <see cref="NowcastCoverage.Missing"/>): the nowcast error never judges them (L5 of the ARV-104g2 review).
    /// </summary>
    NowcastMissing
}

/// <summary>
/// The campaign's targets (ARV-104a: <c>target_bins_per_line</c>, <c>target_tracer_runs</c>, whether they are the placeholder
/// defaults) and the counts of judged items each criterion needs, derived from them (Proposed: a desk-state agreement or a
/// nowcast error over at least as many minutes as the target bins of a line hold, 20 bins of 15 minutes giving 300; track
/// completion over at least the target bins per zone).
/// </summary>
public sealed record CampaignTargets(int BinsPerLine, int TracerRuns, bool Placeholder)
{
    /// <summary>Minutes in a 15-minute bin.</summary>
    public const int MinutesPerBin = 15;

    /// <summary>Good bins a zone's track completion needs (Proposed: the bins per line).</summary>
    public int TrackBins => BinsPerLine;

    /// <summary>Judged desk minutes the desk-state agreement needs (Proposed: the bins per line in minutes).</summary>
    public int DeskMinutes => BinsPerLine * MinutesPerBin;

    /// <summary>Judged nowcast minutes the nowcast error needs (Proposed: the bins per line in minutes).</summary>
    public int NowcastMinutes => BinsPerLine * MinutesPerBin;

    /// <summary>The targets of a campaign.</summary>
    public static CampaignTargets Of(ValidationCampaign campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return new CampaignTargets(campaign.TargetBinsPerLine, campaign.TargetTracerRuns, campaign.TargetsPlaceholder);
    }
}

/// <summary>Settings of the campaign verdict (Proposed, docs/product/decisions.md, ARV-104g2).</summary>
public sealed record CampaignVerdictSettings
{
    /// <summary>How excluded desk minutes and no-service nowcast minutes count (Proposed: <see cref="ExclusionRule.Strict"/>).</summary>
    public ExclusionRule Exclusions { get; init; } = ExclusionRule.Strict;

    /// <summary>With <see cref="ExclusionRule.Capped"/>, the largest excluded share a criterion may hold and still pass (Proposed 5 percent).</summary>
    public double MaxExcludedShare { get; init; } = 0.05;

    public IEnumerable<string> Problems()
    {
        if (!Enum.IsDefined(Exclusions))
            yield return "Exclusions is Strict or Capped.";
        if (!double.IsFinite(MaxExcludedShare) || MaxExcludedShare is < 0 or > 1)
            yield return "MaxExcludedShare is from 0 to 1.";
    }
}

/// <summary>
/// One unit of a criterion judged per unit (a line for count accuracy, a zone for track completion): its figure, the judged
/// items and the count it needs, the excluded items, the engine's verdict over the items it judged, and the unit's verdict
/// (no data below the count needed).
/// </summary>
public sealed record CampaignCriterionUnit(string QueueZone, string LineName, double? Value, int Judged, int Required, int Excluded,
    CriterionVerdict JudgedVerdict, CriterionVerdict Verdict);

/// <summary>
/// A campaign's verdict on one criterion (ARV-104g2, formulas F18 "Campaign verdicts"): the figure the verdict reads
/// (<see cref="Value"/>: under <see cref="ExclusionRule.Strict"/> with the excluded items counted against the system), the
/// figure over judged items alone beside it (<see cref="JudgedValue"/>), the target, the judged items and the count needed,
/// the excluded items and their share (always shown), the verdict and why, and the units for a criterion judged per line or
/// per zone. Not a person in it.
/// </summary>
public sealed record CampaignCriterionVerdict(
    CampaignCriterion Criterion,
    CriterionVerdict Verdict,
    CampaignVerdictReason Reason,
    double? Value,
    double? JudgedValue,
    double Target,
    int Judged,
    int Required,
    int Excluded,
    double? ExcludedShare,
    IReadOnlyList<CampaignCriterionUnit> Units);

/// <summary>
/// The campaign verdicts (ARV-104g2; formulas F18 "Campaign verdicts"; Proposed rules in docs/product/decisions.md). The engine's
/// per-line, per-zone and per-desk verdicts judge only the items they hold; a campaign verdict also needs the campaign's target
/// counts of Good judged items, otherwise no data, and shows the excluded share. A criterion judged per unit (count accuracy per
/// line in scope, track completion per zone that counts tracks) fails when any unit with its count of judged items fails, has
/// no data when any unit lacks its count (or there is no unit), and passes otherwise; the others are judged once over the
/// campaign. The exclusion levers (desk minutes whose observers disagree or whose state cannot be used, nowcast minutes without
/// a number while people waited under the cut) count against the system under <see cref="ExclusionRule.Strict"/> (the
/// Proposed default) or fail the criterion above a cap under <see cref="ExclusionRule.Capped"/>. Pure: no I/O, no clock.
/// </summary>
public static class CampaignVerdicts
{
    #region Criteria

    /// <summary>
    /// Count accuracy (F18, at least 95 percent per 15-minute bin, each line): one unit per line in scope, needing
    /// <see cref="CampaignTargets.BinsPerLine"/> judged bins; its figure the lowest accuracy of its judged items, the campaign's
    /// the lowest over the lines. Excluded bins (not Good, or an observer's count unusable) are shown.
    /// </summary>
    public static CampaignCriterionVerdict CountAccuracy(ComparisonResult result, CampaignTargets targets, ComparisonSettings settings)
    {
        Guard(result, targets, ref settings);
        var target = settings.CountAccuracyTarget;
        if (result.Problem is not null)
            return NotCompared(CampaignCriterion.CountAccuracy, target);
        var units = result.Lines.Select(l => Unit(l.QueueZone, l.LineName, l.LowestAccuracy, l.JudgedBins, targets.BinsPerLine, l.ExcludedBins, l.Check.Verdict)).ToList();
        return PerUnit(CampaignCriterion.CountAccuracy, target, units, Lowest(units));
    }

    /// <summary>
    /// Realised-wait error (F18, within max(1 min, 10 percent of the true wait)): over every zone's compared tracer runs, needing
    /// <see cref="CampaignTargets.TracerRuns"/> of them; the figure is the share within tolerance. Runs neither abandoned nor Good
    /// are the excluded share.
    /// </summary>
    public static CampaignCriterionVerdict WaitError(ComparisonResult result, CampaignTargets targets, ComparisonSettings settings)
    {
        Guard(result, targets, ref settings);
        var target = settings.WithinToleranceTarget;
        if (result.Problem is not null || result.TracerOverall is not { } overall)
            return NotCompared(CampaignCriterion.WaitError, target);
        var share = overall.Compared > 0 ? overall.WithinTolerance / (double)overall.Compared : (double?)null;
        return Once(CampaignCriterion.WaitError, target, share, share, overall.Compared, targets.TracerRuns, overall.Excluded, overall.ErrorCheck.Verdict);
    }

    /// <summary>Realised-wait bias (F18, within plus or minus 5 percent): the same runs and count as <see cref="WaitError"/>.</summary>
    public static CampaignCriterionVerdict WaitBias(ComparisonResult result, CampaignTargets targets, ComparisonSettings settings)
    {
        Guard(result, targets, ref settings);
        var target = settings.BiasTarget;
        if (result.Problem is not null || result.TracerOverall is not { } overall)
            return NotCompared(CampaignCriterion.WaitBias, target);
        return Once(CampaignCriterion.WaitBias, target, overall.Bias, overall.Bias, overall.Compared, targets.TracerRuns, overall.Excluded, overall.BiasCheck.Verdict);
    }

    /// <summary>
    /// Track completion (F18, at least 90 percent): one unit per queue zone in scope, needing <see cref="CampaignTargets.TrackBins"/>
    /// Good bins; its figure the pooled rate over its Good bins, the campaign's the lowest over the zones. Whether a zone's sensors
    /// track people (T3) is not recorded anywhere the service can read, so a zone counts tracks when any track entered in any of
    /// its bins. Fail closed (security review of ARV-104g2, M2, Proposed): a zone without a track all campaign stays a unit with
    /// nothing judged, so when some zones count tracks and another has none the criterion has no data
    /// (<see cref="CampaignVerdictReason.ZonesWithoutTracks"/>, the zones listed among the units) rather than passing on the others;
    /// when no zone counts tracks the criterion is not in scope, with every zone listed. A failing zone still fails the criterion.
    /// </summary>
    public static CampaignCriterionVerdict TrackCompletion(ComparisonResult result, CampaignTargets targets, ComparisonSettings settings)
    {
        Guard(result, targets, ref settings);
        var target = settings.TrackCompletionTarget;
        if (result.Problem is not null)
            return NotCompared(CampaignCriterion.TrackCompletion, target);
        var units = result.TrackCompletion
            .Select(z => (Zone: z, Tracks: HasTracks(z)))
            .Select(z => Unit(z.Zone.QueueZone, null, z.Zone.Good.Rate, z.Tracks ? z.Zone.Good.Bins : 0, targets.TrackBins, z.Zone.ExcludedBins,
                z.Tracks ? z.Zone.Check.Verdict : CriterionVerdict.NoData))
            .ToList();
        var silent = result.TrackCompletion.Count(z => !HasTracks(z));
        if (silent == units.Count)
            return Verdict(CampaignCriterion.TrackCompletion, CriterionVerdict.NoData, CampaignVerdictReason.NotInScope, null, null, target, 0, units.Sum(u => u.Required),
                units.Sum(u => u.Excluded), null, units.AsReadOnly());
        var verdict = PerUnit(CampaignCriterion.TrackCompletion, target, units, Lowest(units));
        return verdict.Verdict == CriterionVerdict.NoData && silent > 0 ? verdict with { Reason = CampaignVerdictReason.ZonesWithoutTracks } : verdict;
    }

    /// <summary>Whether any track entered in any of a zone's bins, whatever their standing.</summary>
    private static bool HasTracks(ZoneTrackCompletion zone) => zone.Good.Entered + zone.Degraded.Entered + zone.Unknown.Entered > 0;

    /// <summary>
    /// Desk-state agreement (F18, D6: at least 95 percent of observed minutes) over every desk in scope, needing
    /// <see cref="CampaignTargets.DeskMinutes"/> judged minutes. <see cref="ExclusionRule.Strict"/>: the figure is the strict
    /// agreement (agreeing minutes over every observed minute, the minutes whose observers disagree and those with an unusable
    /// state counted as disagreements); <see cref="ExclusionRule.Capped"/>: the agreement over judged minutes, failing when the
    /// excluded share is above the cap. Border per-desk data: served only to border roles of the campaign's site (ARV-104g).
    /// Not in scope for a campaign without desks.
    /// </summary>
    public static CampaignCriterionVerdict DeskStateAgreement(ComparisonResult result, CampaignTargets targets, ComparisonSettings settings,
        CampaignVerdictSettings verdictSettings, bool campaignHasDesks)
    {
        Guard(result, targets, ref settings);
        verdictSettings = Checked(verdictSettings);
        var target = settings.DeskAgreementTarget;
        if (!campaignHasDesks)
            return Verdict(CampaignCriterion.DeskStateAgreement, CriterionVerdict.NoData, CampaignVerdictReason.NotInScope, null, null, target, 0, 0, 0, null, []);
        if (result.Problem is not null || result.DeskOverall is not { } desks)
            return NotCompared(CampaignCriterion.DeskStateAgreement, target);
        var share = Share(desks.Excluded, desks.Judged + desks.Excluded);
        return Levered(CampaignCriterion.DeskStateAgreement, target, verdictSettings, desks.StrictAgreement, desks.Check.Value, desks.Judged, targets.DeskMinutes,
            desks.Excluded, share, strictMeets: v => Ariva.Core.Validation.Comparison.CountAccuracy.AtLeast(v, target), desks.Check.Verdict);
    }

    /// <summary>
    /// Nowcast error (F18, D6: median within 2 minutes for waits under 20 minutes) of the published nowcast over every zone,
    /// needing <see cref="CampaignTargets.NowcastMinutes"/> judged minutes. <see cref="ExclusionRule.Strict"/>: every Good minute
    /// without a number whose next minute's entrants waited under the cut counts as an error beyond the target
    /// (<see cref="NowcastErrors.MaxErrorMinutes"/>) in the median; <see cref="ExclusionRule.Capped"/>: the median over judged
    /// minutes, failing when those minutes' share is above the cap.
    /// </summary>
    public static CampaignCriterionVerdict NowcastError(ComparisonResult result, CampaignTargets targets, ComparisonSettings settings,
        CampaignVerdictSettings verdictSettings)
    {
        Guard(result, targets, ref settings);
        verdictSettings = Checked(verdictSettings);
        var target = settings.NowcastErrorTargetMinutes;
        if (result.Problem is not null || result.NowcastOverall is not { } overall)
            return NotCompared(CampaignCriterion.NowcastError, target);
        var published = overall.Published;
        var noService = published.NoServiceUnderCut;
        var strict = NowcastErrors.MedianWithMissing(result.NowcastMinutes, noService);
        var share = Share(noService, published.Judged.Minutes + noService);
        return Levered(CampaignCriterion.NowcastError, target, verdictSettings, strict, published.Judged.MedianAbsoluteErrorMinutes, published.Judged.Minutes,
            targets.NowcastMinutes, noService, share, strictMeets: v => NowcastErrors.IsWithinTarget(v, target), overall.Check.Verdict);
    }

    /// <summary>
    /// Availability during the campaign (F18, D6: 99 percent of operating hours; ARV-118): available operating minutes over
    /// operating minutes of the planned days as the ledger recorded them; no data without an operating minute.
    /// </summary>
    public static CampaignCriterionVerdict Availability(AvailabilityCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var target = AvailabilitySummary.PilotTarget;
        var ratio = counts.Availability;
        var verdict = ratio is { } r ? Ariva.Core.Validation.Comparison.CountAccuracy.AtLeast(r, target) ? CriterionVerdict.Pass : CriterionVerdict.Fail : CriterionVerdict.NoData;
        var reason = verdict switch
        {
            CriterionVerdict.Pass => CampaignVerdictReason.Met,
            CriterionVerdict.Fail => CampaignVerdictReason.NotMet,
            _ => CampaignVerdictReason.TooFewJudged
        };
        return Verdict(CampaignCriterion.Availability, verdict, reason, ratio, ratio, target, counts.OperatingMinutes, 1, counts.OperatingMinutes - counts.AvailableMinutes,
            null, []);
    }

    #endregion

    #region Review

    /// <summary>
    /// What needs a person's review in the results the caller may see (desk keys aside, which only border roles see; the desk
    /// section has its own): quality intervals left out, unusable keys, placeholder targets, a campaign not closed, provisional
    /// items, a ground-truth proof that could not be read, zones without a track, and planned minutes without a published nowcast.
    /// </summary>
    public static IReadOnlyList<CampaignReview> Review(ComparisonResult result, CampaignTargets targets, bool campaignClosed, bool shadowRead,
        IReadOnlyList<NowcastCoverage> coverage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(targets);
        var review = new List<CampaignReview>();
        if (result.LeftOut.QualityIntervals > 0)
            review.Add(CampaignReview.QualityIntervalsLeftOut);
        if (result.LeftOut.UnusableKeys.Any(k => k.Kind is not (UnusableKeyKind.DeskObservation or UnusableKeyKind.DeskMinute)))
            review.Add(CampaignReview.UnusableRows);
        if (targets.Placeholder)
            review.Add(CampaignReview.PlaceholderTargets);
        if (!campaignClosed)
            review.Add(CampaignReview.CampaignNotClosed);
        if (result.CountBins.Any(b => b.Standing == ComparisonStanding.Provisional) || result.Tracers.Any(t => t.Standing == ComparisonStanding.Provisional) ||
            result.TrackCompletion.Any(z => z.Bins.Provisional > 0) || (result.NowcastOverall?.Published.Standings.Provisional ?? 0) > 0)
            review.Add(CampaignReview.ProvisionalResults);
        if (!shadowRead)
            review.Add(CampaignReview.ProofNotRead);
        if (result.TrackCompletion.Any(z => !HasTracks(z)))
            review.Add(CampaignReview.ZonesWithoutTracks);
        if (coverage is not null && coverage.Any(c => c is { Missing: > 0 }))
            review.Add(CampaignReview.NowcastMissing);
        return review.AsReadOnly();
    }

    #endregion

    #region Rules

    private static void Guard(ComparisonResult result, CampaignTargets targets, ref ComparisonSettings settings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(targets);
        settings ??= new ComparisonSettings();
    }

    private static CampaignVerdictSettings Checked(CampaignVerdictSettings settings)
    {
        settings ??= new CampaignVerdictSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
        return settings;
    }

    private static CampaignCriterionUnit Unit(string zone, string line, double? value, int judged, int required, int excluded, CriterionVerdict judgedVerdict) =>
        new(zone, line, value, judged, required, excluded, judgedVerdict, judged < required || judged == 0 ? CriterionVerdict.NoData : judgedVerdict);

    /// <summary>Any unit failing fails the criterion; else any unit without its count (or none at all) gives no data; else it passes.</summary>
    private static CampaignCriterionVerdict PerUnit(CampaignCriterion criterion, double target, List<CampaignCriterionUnit> units, double? value)
    {
        int judged = units.Sum(u => u.Judged), excluded = units.Sum(u => u.Excluded), required = units.Sum(u => u.Required);
        var (verdict, reason) = units.Any(u => u.Verdict == CriterionVerdict.Fail) ? (CriterionVerdict.Fail, CampaignVerdictReason.NotMet)
            : units.Count == 0 || units.Any(u => u.Verdict == CriterionVerdict.NoData) ? (CriterionVerdict.NoData, CampaignVerdictReason.TooFewJudged)
            : (CriterionVerdict.Pass, CampaignVerdictReason.Met);
        return Verdict(criterion, verdict, reason, value, value, target, judged, required, excluded, Share(excluded, judged + excluded), units.AsReadOnly());
    }

    /// <summary>A criterion judged once over the campaign: no data below the count needed, the engine's verdict otherwise.</summary>
    private static CampaignCriterionVerdict Once(CampaignCriterion criterion, double target, double? value, double? judgedValue, int judged, int required, int excluded,
        CriterionVerdict judgedVerdict)
    {
        var (verdict, reason) = judged < required || judged == 0 || judgedVerdict == CriterionVerdict.NoData ? (CriterionVerdict.NoData, CampaignVerdictReason.TooFewJudged)
            : judgedVerdict == CriterionVerdict.Pass ? (CriterionVerdict.Pass, CampaignVerdictReason.Met)
            : (CriterionVerdict.Fail, CampaignVerdictReason.NotMet);
        return Verdict(criterion, verdict, reason, value, judgedValue, target, judged, required, excluded, Share(excluded, judged + excluded), []);
    }

    /// <summary>A criterion with an exclusion lever, judged once under the campaign's <see cref="ExclusionRule"/>.</summary>
    private static CampaignCriterionVerdict Levered(CampaignCriterion criterion, double target, CampaignVerdictSettings settings, double? strictValue, double? judgedValue,
        int judged, int required, int excluded, double? share, Func<double, bool> strictMeets, CriterionVerdict judgedVerdict)
    {
        if (judged < required || judged == 0)
            return Verdict(criterion, CriterionVerdict.NoData, CampaignVerdictReason.TooFewJudged,
                settings.Exclusions == ExclusionRule.Strict ? strictValue : judgedValue, judgedValue, target, judged, required, excluded, share, []);
        if (settings.Exclusions == ExclusionRule.Strict)
        {
            var meets = strictValue is { } v && strictMeets(v);
            return Verdict(criterion, meets ? CriterionVerdict.Pass : CriterionVerdict.Fail, meets ? CampaignVerdictReason.Met : CampaignVerdictReason.NotMet,
                strictValue, judgedValue, target, judged, required, excluded, share, []);
        }

        if (share is { } s && Math.Round(s - settings.MaxExcludedShare, 9) > 0)
            return Verdict(criterion, CriterionVerdict.Fail, CampaignVerdictReason.ExcludedShareAboveCap, judgedValue, judgedValue, target, judged, required, excluded, share, []);
        return Once(criterion, target, judgedValue, judgedValue, judged, required, excluded, judgedVerdict) with { ExcludedShare = share };
    }

    private static CampaignCriterionVerdict NotCompared(CampaignCriterion criterion, double target) =>
        Verdict(criterion, CriterionVerdict.NoData, CampaignVerdictReason.NotCompared, null, null, target, 0, 0, 0, null, []);

    private static CampaignCriterionVerdict Verdict(CampaignCriterion criterion, CriterionVerdict verdict, CampaignVerdictReason reason, double? value, double? judgedValue,
        double target, int judged, int required, int excluded, double? share, IReadOnlyList<CampaignCriterionUnit> units) =>
        new(criterion, verdict, reason, value, judgedValue, target, judged, required, excluded, share, units);

    private static double? Lowest(List<CampaignCriterionUnit> units) =>
        units.Where(u => u.Value is not null).Select(u => u.Value.GetValueOrDefault()).DefaultIfEmpty(double.NaN).Min() is var m && double.IsNaN(m) ? null : m;

    private static double? Share(int part, int whole) => whole > 0 ? part / (double)whole : null;

    #endregion
}
