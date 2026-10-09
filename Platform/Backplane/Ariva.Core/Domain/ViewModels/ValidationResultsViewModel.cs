using Ariva.Core.Availability;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Validation;
using Ariva.Core.Validation.Comparison;

namespace Ariva.Core.Domain.ViewModels;

/// <summary>
/// A validation campaign's results (ARV-104g2; served by ARV-104g): each pilot criterion's campaign verdict against the
/// campaign's targets, the comparison behind it, availability (ARV-118), the calibration records of the devices in scope, the
/// profile version and geometry hash, and the nowcast's coverage. No person is named: observers appear only as pseudonymous
/// Ariva user ids, and only in <see cref="Observers"/> and <see cref="DesksView.ObserverKeys"/>.
/// <para>
/// Shaped for ARV-104g's serving rules, so that each rule drops a section rather than a field:
/// <list type="bullet">
/// <item><see cref="Desks"/> holds every desk-state result (the desk criterion's verdict, per minute, per desk and over every desk,
/// the left-out desk rows and their keys): border per-desk data, only for <c>BorderDesks.View</c> holders of the campaign's site
/// (<c>BorderDeskAccess.Sees</c>), never airport roles, the border-to-airport feed, AMAN or an airport deployment. Nothing outside
/// it says anything about desks (the review flags and left-out counts outside it leave desk rows out).</item>
/// <item><see cref="Nowcast"/> holds the shadow nowcast's figures beside the published one's (the ground-truth proof): validation
/// data, only in these results to <c>Validation.View</c> holders of the site, never on another screen, a display, an alert, a
/// report, the live snapshot, the feed or AMAN (this type is the one projection the exposure tests of the
/// Architecture suite allow to carry them).</item>
/// <item><see cref="Observers"/> holds the observer-level results (each tracer run with its observer, the offset spread per observer
/// and per batch, the unusable keys of manual counts with their observer): only <c>Validation.View</c> or <c>Validation.Manage</c>
/// holders of the site, an observer its own at most; never AMAN or the feed.</item>
/// </list>
/// </para>
/// </summary>
public sealed record ValidationResultsViewModel(
    Guid CampaignId,
    string SiteCode,
    string CampaignName,
    string Status,
    int ProfileVersion,
    string GeometryHash,
    IReadOnlyList<string> PlannedDays,
    string TimeZoneId,
    DateTime ComputedUtc,
    ComparisonProblem? Problem,
    ValidationResultsViewModel.TargetsView Targets,
    IReadOnlyList<CampaignCriterionVerdict> Criteria,
    IReadOnlyList<CampaignReview> Review,
    ValidationResultsViewModel.CountsView Counts,
    ValidationResultsViewModel.TracersView Tracers,
    IReadOnlyList<ZoneTrackCompletion> TrackCompletion,
    ValidationResultsViewModel.NowcastView Nowcast,
    ValidationResultsViewModel.AvailabilityView Availability,
    IReadOnlyList<ValidationResultsViewModel.CalibrationView> Calibrations,
    ValidationResultsViewModel.LeftOutView LeftOut,
    ValidationResultsViewModel.DesksView Desks,
    ValidationResultsViewModel.ObserversView Observers)
{
    #region Sections

    /// <summary>
    /// The targets the verdicts used: the campaign's counts of judged items (and whether they are the placeholder defaults), the
    /// counts derived from them, the criteria's targets (F18 defaults until the KPI annex fixes them per campaign) and the
    /// exclusion rule.
    /// </summary>
    public sealed record TargetsView(
        int BinsPerLine,
        int TracerRuns,
        bool Placeholder,
        int TrackBins,
        int DeskMinutes,
        int NowcastMinutes,
        double CountAccuracy,
        double WithinTolerance,
        double Bias,
        double TrackCompletion,
        double DeskAgreement,
        double NowcastErrorMinutes,
        double NowcastWaitCutMinutes,
        double Availability,
        ExclusionRule Exclusions,
        double MaxExcludedShare);

    /// <summary>Count accuracy per line and per line, bin and direction (no observer: their counts are pooled per item).</summary>
    public sealed record CountsView(IReadOnlyList<LineAccuracy> Lines, IReadOnlyList<LineBinAccuracy> Bins);

    /// <summary>Tracer wait error and bias per zone and over every zone (the runs themselves, with their observers, are in <see cref="Observers"/>).</summary>
    public sealed record TracersView(IReadOnlyList<TracerZoneSummary> Zones, TracerZoneSummary Overall);

    /// <summary>
    /// The nowcast error per zone and over every zone with the ground-truth proof (published and shadow side by side), every
    /// compared minute, and the published nowcast's coverage per zone and over every zone. <see cref="ShadowRead"/> is false
    /// when no validation reader login was configured, so no shadow figure could be read (ARV-104g1).
    /// </summary>
    public sealed record NowcastView(
        bool ShadowRead,
        IReadOnlyList<NowcastZoneErrors> Zones,
        NowcastZoneErrors Overall,
        IReadOnlyList<NowcastMinuteError> Minutes,
        IReadOnlyList<NowcastCoverage> Coverage,
        NowcastCoverage CoverageOverall);

    /// <summary>The availability ledger over the campaign's planned days (ARV-118): the pilot target, the total and each day.</summary>
    public sealed record AvailabilityView(double Target, AvailabilityCounts Total, IReadOnlyList<AvailabilityDay> Days);

    /// <summary>
    /// A calibration record of a device in scope (ARV-021, script 0013): the device's code and queue zone, the method, sample,
    /// measured accuracy and wait error, the threshold and the verdict, and when it was performed. Notes and the person who
    /// recorded it are left out.
    /// </summary>
    public sealed record CalibrationView(
        string DeviceCode,
        string QueueZone,
        string Method,
        int SampleSize,
        double CountingAccuracyPercent,
        double WaitTimeErrorMinutes,
        double ThresholdPercent,
        bool Passed,
        DateTime PerformedOn);

    /// <summary>
    /// Rows the comparison left out per kind (desk rows aside: they are in <see cref="Desks"/>) and the unusable keys of those
    /// kinds; a manual count's key without its observer (that is in <see cref="ObserversView.CountKeys"/>).
    /// </summary>
    public sealed record LeftOutView(
        int ManualCounts,
        int TracerRuns,
        int LineBins,
        int QueueMinutes,
        int QueueBins,
        int HealthBins,
        int QualityIntervals,
        int ShadowMinutes,
        IReadOnlyList<UnusableKey> Keys);

    /// <summary>
    /// Every desk-state result (border per-desk data; see the type): the desk-state agreement's campaign verdict, each observed
    /// desk minute, each desk, every desk, the desk rows left out, their unusable keys without observers, and the unusable keys of
    /// desk states with their observers (<see cref="ObserverKeys"/>: border data and observer-level at once).
    /// </summary>
    public sealed record DesksView(
        CampaignCriterionVerdict Verdict,
        IReadOnlyList<DeskMinuteAgreement> Minutes,
        IReadOnlyList<DeskAgreementSummary> Desks,
        DeskAgreementSummary Overall,
        int ObservationsLeftOut,
        int DeskMinutesLeftOut,
        IReadOnlyList<UnusableKey> Keys,
        IReadOnlyList<UnusableKey> ObserverKeys);

    /// <summary>
    /// The observer-level results (see the type): each tracer run with its observer, the offset spread per observer and per batch,
    /// and the unusable keys of manual counts with their observer.
    /// </summary>
    public sealed record ObserversView(
        IReadOnlyList<TracerComparison> Runs,
        IReadOnlyList<ObserverOffsetSpread> Offsets,
        IReadOnlyList<BatchOffset> Batches,
        IReadOnlyList<UnusableKey> CountKeys);

    #endregion

    #region Mapping

    /// <summary>
    /// The campaign as the results name it (ARV-104a): id, site, name, status, profile version and geometry hash, planned days,
    /// its targets of judged items and whether it was planned with desks.
    /// </summary>
    public sealed record CampaignFacts(
        Guid Id,
        string SiteCode,
        string Name,
        ValidationCampaignStatus Status,
        int ProfileVersion,
        string GeometryHash,
        IReadOnlyList<string> PlannedDays,
        CampaignTargets Targets,
        bool HasDesks)
    {
        public static CampaignFacts Of(ValidationCampaign campaign)
        {
            ArgumentNullException.ThrowIfNull(campaign);
            return new CampaignFacts(campaign.Id.GetValueOrDefault(), campaign.SiteCode, campaign.Name, campaign.Status, campaign.ProfileVersion, campaign.GeometryHash,
                [.. campaign.Days.Select(ValidationCampaign.FormatDay)], CampaignTargets.Of(campaign), campaign.Desks.Count > 0);
        }
    }

    /// <summary>What the results are built from, besides the comparison (ARV-104g2).</summary>
    public sealed record Sources(
        CampaignFacts Campaign,
        string TimeZoneId,
        DateTime ComputedUtc,
        ComparisonSettings Settings,
        CampaignVerdictSettings VerdictSettings,
        bool ShadowRead,
        IReadOnlyList<NowcastCoverage> Coverage,
        AvailabilityView Availability,
        IReadOnlyList<CalibrationView> Calibrations);

    /// <summary>The results of a campaign from its pooled comparison (ARV-104g2), its verdicts computed by <see cref="CampaignVerdicts"/>.</summary>
    public static ValidationResultsViewModel From(ComparisonResult result, Sources sources)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sources.Campaign);
        var campaign = sources.Campaign;
        var settings = sources.Settings ?? new ComparisonSettings();
        var verdictSettings = sources.VerdictSettings ?? new CampaignVerdictSettings();
        var targets = campaign.Targets;
        var availability = sources.Availability ?? new AvailabilityView(AvailabilitySummary.PilotTarget, AvailabilityCounts.Zero, []);
        var coverage = result.Problem is null ? sources.Coverage ?? [] : [];

        var criteria = new List<CampaignCriterionVerdict>
        {
            CampaignVerdicts.CountAccuracy(result, targets, settings),
            CampaignVerdicts.WaitError(result, targets, settings),
            CampaignVerdicts.WaitBias(result, targets, settings),
            CampaignVerdicts.TrackCompletion(result, targets, settings),
            CampaignVerdicts.NowcastError(result, targets, settings, verdictSettings),
            CampaignVerdicts.Availability(availability.Total)
        };

        var keys = result.LeftOut.UnusableKeys;
        static bool IsDesk(UnusableKey k) => k.Kind is UnusableKeyKind.DeskObservation or UnusableKeyKind.DeskMinute;
        var left = result.LeftOut;
        return new ValidationResultsViewModel(
            campaign.Id, campaign.SiteCode, campaign.Name, campaign.Status.ToString(), campaign.ProfileVersion, campaign.GeometryHash,
            campaign.PlannedDays, sources.TimeZoneId, sources.ComputedUtc, result.Problem,
            new TargetsView(targets.BinsPerLine, targets.TracerRuns, targets.Placeholder, targets.TrackBins, targets.DeskMinutes, targets.NowcastMinutes,
                settings.CountAccuracyTarget, settings.WithinToleranceTarget, settings.BiasTarget, settings.TrackCompletionTarget, settings.DeskAgreementTarget,
                settings.NowcastErrorTargetMinutes, settings.NowcastWaitCutMinutes, AvailabilitySummary.PilotTarget, verdictSettings.Exclusions,
                verdictSettings.MaxExcludedShare),
            criteria.AsReadOnly(),
            CampaignVerdicts.Review(result, targets, campaign.Status == ValidationCampaignStatus.Closed, sources.ShadowRead, coverage),
            new CountsView(result.Lines, result.CountBins),
            new TracersView(result.TracerZones, result.TracerOverall),
            result.TrackCompletion,
            new NowcastView(sources.ShadowRead, result.NowcastZones, result.NowcastOverall, result.NowcastMinutes, coverage, NowcastCoverage.Sum(coverage)),
            availability,
            sources.Calibrations ?? [],
            new LeftOutView(left.ManualCounts, left.TracerRuns, left.LineBins, left.QueueMinutes, left.QueueBins, left.HealthBins, left.QualityIntervals,
                left.ShadowMinutes, [.. keys.Where(k => !IsDesk(k)).Select(k => k with { ObserverId = null })]),
            new DesksView(
                CampaignVerdicts.DeskStateAgreement(result, targets, settings, verdictSettings, campaign.HasDesks),
                result.DeskMinutes, result.Desks, result.DeskOverall, left.DeskObservations, left.DeskMinutes,
                [.. keys.Where(IsDesk).Select(k => k with { ObserverId = null })],
                [.. keys.Where(k => k.Kind == UnusableKeyKind.DeskObservation)]),
            new ObserversView(result.Tracers, result.Observers, result.Batches, [.. keys.Where(k => k.Kind == UnusableKeyKind.ManualCount)]));
    }

    #endregion
}
