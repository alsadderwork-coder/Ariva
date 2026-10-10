using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Validation;
using Ariva.Core.Validation.Comparison;
using Microsoft.Extensions.DependencyInjection;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// The computation of a campaign's results (ARV-104g2, <see cref="ISvcValidationResults"/>; served and frozen by
/// <c>SvcValidationResults.Revisions.cs</c>, ARV-104g): the site and the campaign are
/// checked against the caller's sites first (ISiteScope; another site's campaign answers NotFound, CWE-863, CWE-204); then one
/// computation per campaign runs apart from its callers in a scope of its own (<see cref="SingleFlight{T}"/>, a timeout and a
/// host-wide limit), reading only the campaign's site and the campaign's own rows (the site code and campaign id the caller's
/// check confirmed), comparing in slices (<see cref="ComparisonSlices"/>) and building the verdicts and the evidence beside
/// them (<see cref="ValidationResultsViewModel.From"/>).
/// </summary>
internal sealed partial class SvcValidationResults
{
    #region Constants

    /// <summary>Queue minutes read either side of the planned days: the next minute a nowcast is compared with, and the tracers' reach.</summary>
    private static readonly TimeSpan MinuteMargin = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Queue bins read before and after the planned days: the bin a margin minute falls in, and after them the bins covering the
    /// realised wait of the last minutes' entrants (at most a day, F7) and a tracer's wait (at most 3 hours, script 0048).
    /// </summary>
    private static readonly TimeSpan BinMarginBefore = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan BinMarginAfter = TimeSpan.FromDays(1) + TimeSpan.FromMinutes(15);

    #endregion

    #region Service

    /// <summary>
    /// The computation in a scope of its own (its own unit of work), so no caller's request scope ends under it. The caller has
    /// checked the site and the campaign against its own sites (ARV-104g: <see cref="GetAsync"/>, <see cref="RecomputeAsync"/>).
    /// </summary>
    private async Task<Result<ValidationResultsViewModel>> ComputeApartAsync(string siteCode, Guid campaignId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<SvcValidationResults>();
        return await Guarded(() => service.ComputeAsync(siteCode, campaignId, ct), logger, ct);
    }

    /// <summary>
    /// A boundary (L7): the runtime login's pool all in use, or a connection that cannot be opened (an <c>NpgsqlException</c>, as
    /// thrown or wrapped by NHibernate), is an error result for every caller of the flight, not an exception; the log names its
    /// kind only. A cancellation is not caught.
    /// </summary>
    internal static async Task<Result<T>> Guarded<T>(Func<Task<Result<T>>> work, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            return await work();
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is Npgsql.NpgsqlException || e.InnerException is Npgsql.NpgsqlException))
        {
            logger?.LogWarning("The validation results of a campaign could not be read: {Failure}", e.GetType().Name);
            return Result.Error<T>(ValidationResultsErrors.Busy);
        }
    }

    /// <summary>
    /// The campaign's results, read and compared now. The campaign is read by its id and site exactly; the caller has checked that
    /// site against its own (<see cref="GetAsync"/>), so this runs without a caller.
    /// </summary>
    internal async Task<Result<ValidationResultsViewModel>> ComputeAsync(string siteCode, Guid campaignId, CancellationToken ct)
    {
        var campaign = await CampaignByIdAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<ValidationResultsViewModel>(ValidationErrors.NotFound);
        var zone = await TimeZoneAsync(siteCode, ct);
        var compared = await CompareAsync(campaign, zone, sliced: true, ct);
        if (compared.HasErrors)
            return Result.Error<ValidationResultsViewModel>(compared.ErrorMessages);

        var availability = await AvailabilityAsync(campaign, ct);
        var windows = ComparisonScope.WindowsOf(campaign.Days, zone);
        var calibrations = await CalibrationsAsync(campaign, windows.Count > 0 ? windows.Max(w => w.ToUtc) : UtcNow, ct);
        if (calibrations is null)
            return Result.Error<ValidationResultsViewModel>(ValidationResultsErrors.TooLarge);

        var (result, coverage, shadowRead) = compared.Data;
        return new Result<ValidationResultsViewModel>(ValidationResultsViewModel.From(result, new ValidationResultsViewModel.Sources(
            ValidationResultsViewModel.CampaignFacts.Of(campaign), zone.Id, UtcNow, Settings, settings?.Verdicts, shadowRead, coverage, availability, calibrations)));
    }

    /// <summary>The campaign with this id at this site exactly (no caller: the caller's sites were checked before the flight); null otherwise.</summary>
    internal Task<ValidationCampaign> CampaignByIdAsync(string siteCode, Guid campaignId, CancellationToken ct) =>
        Query<ValidationCampaign>().FirstOrDefaultAsync(c => c.Id == campaignId && c.SiteCode == siteCode, ct);

    /// <summary>The comparison's settings: F18's targets (the KPI annex's per campaign once agreed) and the Proposed checks.</summary>
    private static ComparisonSettings Settings { get; } = new();

    #endregion

    #region Comparison

    /// <summary>The campaign's comparison read by its id and site exactly, in slices or whole (for the tests that prove the two equal).</summary>
    internal async Task<Result<Compared>> CompareAsync(string siteCode, Guid campaignId, bool sliced, CancellationToken ct)
    {
        var campaign = await CampaignByIdAsync(siteCode, campaignId, ct);
        return campaign is null
            ? Result.Error<Compared>(ValidationErrors.NotFound)
            : await CompareAsync(campaign, await TimeZoneAsync(siteCode, ct), sliced, ct);
    }

    /// <summary>
    /// The campaign's comparison and each zone's nowcast coverage, and whether the shadow nowcast was read. In slices
    /// (<paramref name="sliced"/>): the base over the full scope with only the queue minutes the tracers read, then one zone at a
    /// time with its minutes and shadow nowcasts (dropped once compared), pooled (<see cref="ComparisonSlices.Pool"/>). Whole: every
    /// row read the same way, compared at once (tests prove the two equal on a campaign within the engine's bounds). An error when
    /// a read is beyond its bound or the shadow cannot be read; a shadow without a reader login configured is no shadow at all.
    /// </summary>
    internal async Task<Result<Compared>> CompareAsync(ValidationCampaign campaign,
        TimeZoneInfo siteZone, bool sliced, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        // ARV-104g (M3 of the ARV-104g2 review): a campaign beyond the zone-days a computation may hold in memory is refused before
        // any row is read (campaigns planned before the bound existed; new ones are refused at planning).
        if (!ValidationCampaign.IsWithinZoneDays(campaign.Zones.Count, campaign.Days.Count))
            return Result.Error<Compared>(ValidationResultsErrors.TooManyZoneDays);
        var tooLarge = Result.Error<Compared>(ValidationResultsErrors.TooLarge);
        var site = campaign.SiteCode;
        var scope = ComparisonScope.Of(campaign, siteZone);
        var zones = ComparisonSlices.ZonesOf(scope);
        var zonesByKey = zones.ToDictionary(z => ZoneKeys.For(site, z.Name), z => z.Name, StringComparer.Ordinal);
        var planned = Merged(scope.Windows, TimeSpan.Zero, TimeSpan.Zero);
        var shadowRead = reader.IsConfigured;

        if (CollidingDeskKeys(site, campaign.Desks.Select(d => (d.CheckpointCode, d.DeskCode))))
            return Result.Error<Compared>(ValidationResultsErrors.DeskKeysCollide);

        // Ground truth, by campaign and site.
        var counts = await ManualCountsAsync(campaign, ct);
        var runs = await TracerRunsAsync(campaign, ct);
        var observations = await DeskObservationsAsync(campaign, ct);
        if (counts is null || runs is null || observations is null)
            return tooLarge;

        // Stored outputs other than minutes and bins, one planned day at a time (a read stays within the rows one read may hold).
        var lineNames = scope.Lines.Select(l => l.Name).Distinct(StringComparer.Ordinal).ToList();
        var lineBins = new List<LineBinCount>();
        var health = new List<Ariva.Core.Queueing.ZoneHealthBin>();
        var deskMinutes = new List<DeskMinuteRow>();
        foreach (var window in scope.Windows)
        {
            var lines = await LineBinsAsync(zonesByKey, lineNames, window, ct);
            var bins = await HealthBinsAsync(zonesByKey, window, ct);
            if (lines is null || bins is null || !Within(lineBins, lines) || !Within(health, bins))
                return tooLarge;
            foreach (var half in HalfDays(window))
            {
                var desks = await DeskMinutesAsync(campaign, half, ct);
                if (desks is null || !Within(deskMinutes, desks))
                    return tooLarge;
            }
        }

        List<QualityInterval> intervals = planned.Count == 0
            ? []
            : await OutagesAsync(zonesByKey, new UtcWindow(planned[0].FromUtc - BinMarginBefore, planned[^1].ToUtc + BinMarginAfter), ct);
        if (intervals is null)
            return tooLarge;

        // Bins and minutes, one zone at a time: the queue bins and minutes with their margins and the shadow nowcasts of the planned days.
        var queueBins = new List<QueueBinRow>();
        var tracerMinutes = ComparisonSlices.TracerMinutes(scope, runs);
        var baseMinutes = new List<QueueMinuteRow>();
        var allMinutes = new List<QueueMinuteRow>();
        var allShadows = new List<ShadowMinuteRow>();
        var slices = new List<ZoneSlice>();
        foreach (var zone in zones)
        {
            var minutes = new List<QueueMinuteRow>();
            foreach (var window in Merged(scope.Windows, MinuteMargin, MinuteMargin))
            {
                var read = await QueueMinutesAsync(site, zone.Name, window, ct);
                if (read is null || !Within(minutes, read))
                    return tooLarge;
            }

            // The zone's bins, with the margins the matching rule and the nowcast's entrants need.
            var zoneKey = new Dictionary<string, string>(StringComparer.Ordinal) { [ZoneKeys.For(site, zone.Name)] = zone.Name };
            var zoneBins = new List<QueueBinRow>();
            foreach (var window in Merged(scope.Windows, BinMarginBefore, BinMarginAfter))
            {
                var read = await QueueBinsAsync(zoneKey, window, ct);
                if (read is null || !Within(zoneBins, read))
                    return tooLarge;
            }

            if (!Within(queueBins, zoneBins))
                return tooLarge;

            var shadows = new List<ShadowMinuteRow>();
            if (shadowRead)
            {
                foreach (var window in planned)
                {
                    var read = await ReadShadowAsync(site, [zone.Name], window.FromUtc, window.ToUtc, ct);
                    if (read.HasErrors)
                        return Result.Error<Compared>(read.ErrorMessages);
                    if (!Within(shadows, read.Data))
                        return tooLarge;
                }
            }

            if (sliced)
            {
                var input = new ComparisonInput
                {
                    Scope = ComparisonSlices.ZoneScope(scope, zone),
                    QueueMinutes = minutes,
                    ShadowMinutes = shadows,
                    QueueBins = zoneBins,
                    QualityIntervals = [.. intervals.Where(i => i.QueueZone == zone.Name)]
                };
                slices.Add(ComparisonSlices.CompareZone(input, Settings));
                baseMinutes.AddRange(ComparisonSlices.ForTracers(minutes, tracerMinutes));
            }
            else if (!Within(allMinutes, minutes) || !Within(allShadows, shadows))
            {
                return tooLarge;
            }
        }

        var whole = new ComparisonInput
        {
            Scope = scope,
            ManualCounts = counts,
            TracerRuns = runs,
            LineBins = lineBins,
            QueueMinutes = sliced ? baseMinutes : allMinutes,
            QueueBins = queueBins,
            HealthBins = health,
            QualityIntervals = intervals,
            DeskObservations = observations,
            DeskMinutes = deskMinutes,
            ShadowMinutes = sliced ? [] : allShadows
        };
        if (!sliced)
            return new Result<Compared>(new Compared(ValidationComparison.Compare(whole, Settings), ComparisonSlices.Coverage(whole, Settings), shadowRead));

        var pooled = ComparisonSlices.Pool(ValidationComparison.Compare(whole, Settings), [.. slices.Select(s => s.Result)], Settings);
        IReadOnlyList<NowcastCoverage> coverage = pooled.Problem is null ? [.. slices.Where(s => s.Coverage is not null).Select(s => s.Coverage)] : [];
        return new Result<Compared>(new Compared(pooled, coverage, shadowRead));
    }

    /// <summary>A campaign's comparison, each zone's nowcast coverage, and whether the shadow nowcast was read.</summary>
    internal sealed record Compared(ComparisonResult Result, IReadOnlyList<NowcastCoverage> Coverage, bool ShadowRead);

    /// <summary>Adds a read's rows to a kind's rows unless they would pass the engine's bound for one kind (<see cref="MaxRows"/>: then nothing could be compared).</summary>
    private static bool Within<T>(List<T> rows, IReadOnlyCollection<T> read)
    {
        if (rows.Count + read.Count > MaxRows)
            return false;
        rows.AddRange(read);
        return true;
    }

    /// <summary>The planned days' windows widened by the margins and merged where they meet, in time order.</summary>
    private static List<UtcWindow> Merged(IReadOnlyList<UtcWindow> windows, TimeSpan before, TimeSpan after)
    {
        var merged = new List<UtcWindow>();
        foreach (var w in windows.OrderBy(w => w.FromUtc))
        {
            var (from, to) = (w.FromUtc - before, w.ToUtc + after);
            if (merged.Count > 0 && from <= merged[^1].ToUtc)
                merged[^1] = merged[^1] with { ToUtc = to > merged[^1].ToUtc ? to : merged[^1].ToUtc };
            else
                merged.Add(new UtcWindow(from, to));
        }

        return merged;
    }

    #endregion
}
