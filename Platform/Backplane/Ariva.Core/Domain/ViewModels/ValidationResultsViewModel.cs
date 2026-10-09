using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Availability;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
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
/// <para>
/// ARV-104g: <see cref="For"/> applies those rules per caller (<see cref="Reader"/>, from the caller's stored roles), and the
/// per-minute lists (every nowcast minute, every observed desk minute) are not part of the results at all (M3 of the ARV-104g2
/// security review: about 2.2 million nowcast minutes at the campaign bounds, some 300 bytes each in memory and 390 bytes each
/// in JSON); the summaries per zone, per desk and over every zone or desk carry their counts. The results are frozen at the
/// campaign's close as revision 1 of a stored document (<see cref="ToDocument"/>, script 0050) with a SHA-256 content hash
/// (<see cref="Hash"/>); <see cref="Revision"/> says which revision a caller reads and <see cref="Audience"/> what the
/// projection left in.
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
    #region Serving (ARV-104g)

    /// <summary>
    /// Which frozen revision the caller reads (ARV-104g; script 0050): null for the results of a campaign that is not closed,
    /// computed when asked and never stored. Not part of the stored document (it is null there; the hash covers the document).
    /// </summary>
    public RevisionView Revision { get; init; }

    /// <summary>What the per-caller projection (<see cref="For"/>) left in; null in the stored document.</summary>
    public AudienceView Audience { get; init; }

    /// <summary>
    /// A frozen revision (script 0050): its number (1 at the campaign's close, later ones from a recomputation with a reason, never
    /// an edit), how many revisions the campaign's results have, the SHA-256 of the stored document (lowercase hexadecimal, over
    /// its exact bytes, checked by the database at insert and by the service at every read), when it was frozen, and the reason
    /// of a recomputation (null for revision 1).
    /// </summary>
    public sealed record RevisionView(int Number, int Revisions, string ContentSha256, DateTime FrozenUtc, string Reason);

    /// <summary>Which sections the caller's projection kept (<see cref="For"/>): desk-state results, observer-level results, the shadow's figures.</summary>
    public sealed record AudienceView(bool DesksIncluded, ObserverResults Observers, bool ProofIncluded);

    /// <summary>How much of the observer-level results a caller reads.</summary>
    public enum ObserverResults
    {
        /// <summary>None: neither <c>Validation.View</c> nor <c>Validation.Manage</c>, nor an observer of the campaign.</summary>
        None,

        /// <summary>An observer (<c>Validation.Capture</c> only): its own runs, offsets, batches and keys at most.</summary>
        Own,

        /// <summary><c>Validation.View</c> or <c>Validation.Manage</c> holders of the campaign's site: every observer's.</summary>
        All
    }

    /// <summary>
    /// Who reads the results (ARV-104g serving rules; data boundary, CWE-863), from the caller's stored roles and its Ariva user
    /// id. The caller has already been checked against the campaign's site (ISiteScope, NotFound outside), so a permission held
    /// here is held at that site.
    /// </summary>
    /// <param name="SeesDesks">Desk-state results and desk keys: <c>BorderDesks.View</c> holders only (<see cref="BorderDeskAccess.Sees"/>), never airport roles.</param>
    /// <param name="Observers">Observer-level results: every observer's, one's own, or none.</param>
    /// <param name="ObserverId">The caller's Ariva user id, for <see cref="ObserverResults.Own"/>.</param>
    /// <param name="SeesProof">The shadow nowcast's figures (the ground-truth proof): <c>Validation.View</c> holders only.</param>
    public sealed record Reader(bool SeesDesks, ObserverResults Observers, Guid? ObserverId, bool SeesProof)
    {
        /// <summary>Nothing beyond the sections every reader gets (default deny).</summary>
        public static Reader Nobody { get; } = new(false, ObserverResults.None, null, false);

        /// <summary>
        /// The reader a caller is, from its stored role codes (null and repeated codes ignored; an unknown code grants nothing) and
        /// its Ariva user id: desks by <see cref="BorderDeskAccess.Of"/>, observer-level results by <c>Validation.View</c> or
        /// <c>Validation.Manage</c> (every observer's) or <c>Validation.Capture</c> with a user id (its own), the proof by
        /// <c>Validation.View</c>.
        /// </summary>
        public static Reader Of(IEnumerable<string> roles, Guid? userId)
        {
            var held = (roles ?? []).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToList();
            if (held.Count == 0)
                return Nobody;
            var permissions = RolePermissions.For(held);
            var view = permissions.Contains(Global.Defaults.Permissions.ViewValidation);
            var observers = view || permissions.Contains(Global.Defaults.Permissions.ManageValidation) ? ObserverResults.All
                : permissions.Contains(Global.Defaults.Permissions.CaptureValidation) && userId is { } id && id != Guid.Empty ? ObserverResults.Own
                : ObserverResults.None;
            return new Reader(BorderDeskAccess.Of(held).Sees, observers, observers == ObserverResults.Own ? userId : null, view);
        }
    }

    /// <summary>
    /// The results as <paramref name="reader"/> may read them (ARV-104g serving rules), a new value (this one is shared between
    /// callers and never changed): <see cref="Desks"/> only to a reader who sees border desks (null otherwise); the observer-level
    /// results (<see cref="Observers"/>, and the desk-state keys with their observers) every observer's, the reader's own, or
    /// none (<see cref="Observers"/> null); the shadow's figures (each zone's and the overall shadow summary and the paired
    /// errors, the left-out shadow minutes and their keys, the review flag about the proof) only to a reader with the proof.
    /// <see cref="Audience"/> says what was kept.
    /// </summary>
    public ValidationResultsViewModel For(Reader reader)
    {
        reader ??= Reader.Nobody;
        var observers = reader.Observers switch
        {
            ObserverResults.All => Observers,
            ObserverResults.Own when reader.ObserverId is { } own && Observers is not null => new ObserversView(
                [.. (Observers.Runs ?? []).Where(r => r.ObserverId == own)],
                [.. (Observers.Offsets ?? []).Where(o => o.ObserverId == own)],
                [.. (Observers.Batches ?? []).Where(b => b.ObserverId == own)],
                [.. (Observers.CountKeys ?? []).Where(k => k.ObserverId == own)]),
            _ => null
        };
        var desks = reader.SeesDesks && Desks is not null
            ? Desks with
            {
                ObserverKeys = reader.Observers switch
                {
                    ObserverResults.All => Desks.ObserverKeys,
                    ObserverResults.Own when reader.ObserverId is { } own => [.. (Desks.ObserverKeys ?? []).Where(k => k.ObserverId == own)],
                    _ => []
                }
            }
            : null;
        var projected = this with { Desks = desks, Observers = observers };
        if (!reader.SeesProof)
        {
            projected = projected with
            {
                Nowcast = Nowcast is null ? null : Nowcast with
                {
                    Zones = [.. (Nowcast.Zones ?? []).Select(WithoutProof)],
                    Overall = WithoutProof(Nowcast.Overall)
                },
                LeftOut = LeftOut is null ? null : LeftOut with
                {
                    ShadowMinutes = 0,
                    Keys = [.. (LeftOut.Keys ?? []).Where(k => k.Kind != UnusableKeyKind.ShadowMinute)]
                },
                Review = [.. (Review ?? []).Where(r => r != CampaignReview.ProofNotRead)]
            };
        }

        return projected with
        {
            Audience = new AudienceView(desks is not null, observers is null ? ObserverResults.None : reader.Observers, reader.SeesProof)
        };
    }

    private static NowcastZoneErrors WithoutProof(NowcastZoneErrors zone) => zone is null ? null : zone with { Shadow = null, Both = null };

    #endregion

    #region Stored document (ARV-104g)

    /// <summary>The most bytes a stored document may hold (script 0050 checks the same bound).</summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    /// <summary>The most revisions a campaign's results may have (script 0050 checks the same bound).</summary>
    public const int MaxRevisions = 1_000;

    /// <summary>The longest reason of a recomputation (script 0050 checks the same bound).</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// The JSON of the stored document and of the served results: camelCase names, enums by name (a stored document keeps its
    /// meaning if an enum is renumbered), no indentation, at most 64 levels deep. One format, so a document read back and written
    /// again gives the same bytes.
    /// </summary>
    private static readonly JsonSerializerOptions DocumentJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 64,
        WriteIndented = false
    };

    /// <summary>
    /// The stored document of these results: their JSON without <see cref="Revision"/> and <see cref="Audience"/> (both null), the
    /// bytes the content hash is taken over. Every section is in it; the serving rules apply when it is read (<see cref="For"/>).
    /// </summary>
    public byte[] ToDocument() => JsonSerializer.SerializeToUtf8Bytes(this with { Revision = null, Audience = null }, DocumentJson);

    /// <summary>These results as served (with <see cref="Revision"/> and <see cref="Audience"/> as set), in the document's JSON format.</summary>
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, DocumentJson);

    /// <summary>
    /// Results read back from a stored document or served JSON; null when the bytes are not such results (not JSON, deeper than
    /// the bound, or without a campaign, a site or criteria).
    /// </summary>
    public static ValidationResultsViewModel FromJson(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var results = JsonSerializer.Deserialize<ValidationResultsViewModel>(utf8, DocumentJson);
            return results is { SiteCode: not null, Criteria: not null } && results.CampaignId != Guid.Empty ? results : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The content hash of a stored document: SHA-256 of its exact bytes, lowercase hexadecimal (as script 0050 computes it).</summary>
    public static string Hash(ReadOnlySpan<byte> document) => Convert.ToHexStringLower(SHA256.HashData(document));

    #endregion

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
    /// The nowcast error per zone and over every zone with the ground-truth proof (published and shadow side by side), and the
    /// published nowcast's coverage per zone and over every zone. <see cref="ShadowRead"/> is false when no validation reader
    /// login was configured, so no shadow figure could be read (ARV-104g1). The compared minutes themselves are not listed (M3,
    /// ARV-104g): each summary counts them.
    /// </summary>
    public sealed record NowcastView(
        bool ShadowRead,
        IReadOnlyList<NowcastZoneErrors> Zones,
        NowcastZoneErrors Overall,
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
    /// Every desk-state result (border per-desk data; see the type): the desk-state agreement's campaign verdict, each desk, every
    /// desk, the desk rows left out, their unusable keys without observers, and the unusable keys of desk states with their
    /// observers (<see cref="ObserverKeys"/>: border data and observer-level at once). The observed minutes themselves are not
    /// listed (M3, ARV-104g): each desk's summary counts them.
    /// </summary>
    public sealed record DesksView(
        CampaignCriterionVerdict Verdict,
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
            new NowcastView(sources.ShadowRead, result.NowcastZones, result.NowcastOverall, coverage, NowcastCoverage.Sum(coverage)),
            availability,
            sources.Calibrations ?? [],
            new LeftOutView(left.ManualCounts, left.TracerRuns, left.LineBins, left.QueueMinutes, left.QueueBins, left.HealthBins, left.QualityIntervals,
                left.ShadowMinutes, [.. keys.Where(k => !IsDesk(k)).Select(k => k with { ObserverId = null })]),
            new DesksView(
                CampaignVerdicts.DeskStateAgreement(result, targets, settings, verdictSettings, campaign.HasDesks),
                result.Desks, result.DeskOverall, left.DeskObservations, left.DeskMinutes,
                [.. keys.Where(IsDesk).Select(k => k with { ObserverId = null })],
                [.. keys.Where(k => k.Kind == UnusableKeyKind.DeskObservation)]),
            new ObserversView(result.Tracers, result.Observers, result.Batches, [.. keys.Where(k => k.Kind == UnusableKeyKind.ManualCount)]));
    }

    #endregion
}
