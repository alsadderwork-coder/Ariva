using System.Globalization;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Validation;

/// <summary>
/// The request rules of the validation campaign API (manual counts ARV-104a, tracer runs and desk observations ARV-104b;
/// Fx.Specification), checked before any entity
/// is touched. Rules that need stored state (the profile version, the scope, the campaign's status, an earlier count) are the
/// entity's and the service's.
/// </summary>
public static class ValidationRules
{
    public static ISpecification<CreateValidationCampaignRequest> Create(DateOnly today) =>
        Fx.Specification<CreateValidationCampaignRequest>()
            .And(r => IsText(r.Name, ValidationCampaign.MaxNameLength, required: true), ValidationErrors.InvalidName)
            .And(r => r.ProfileVersion is >= 1, ValidationErrors.InvalidProfileVersion)
            .And(r => r.ZoneIds is { Count: >= 1 and <= ValidationCampaign.MaxZones } && r.LineIds is { Count: <= ValidationCampaign.MaxLines } &&
                      !r.ZoneIds.Contains(Guid.Empty) && !r.LineIds.Contains(Guid.Empty), ValidationErrors.InvalidScope)
            .And(r => Days(r.Days) is { } days && ValidationCampaign.AreValidDays(days, today), ValidationErrors.InvalidDays)
            .And(r => r.ZoneIds is null || r.Days is null || ValidationCampaign.IsWithinZoneDays(r.ZoneIds.Count, r.Days.Count), ValidationErrors.TooManyZoneDays)
            .And(r => ValidationCampaign.AreValidTargets(r.TargetBinsPerLine, r.TargetTracerRuns), ValidationErrors.InvalidTargets)
            .And(r => r.DeskIds is null || (r.DeskIds.Count <= ValidationCampaign.MaxDesks && !r.DeskIds.Contains(Guid.Empty) &&
                                            r.DeskIds.Distinct().Count() == r.DeskIds.Count), ValidationErrors.InvalidDesks);

    public static ISpecification<CaptureManualCountRequest> Capture() =>
        Fx.Specification<CaptureManualCountRequest>()
            .And(r => r.LineId is { } id && id != Guid.Empty, ValidationErrors.LineNotInScope)
            .And(r => BinStart(r.BinStartUtc) is not null, ValidationErrors.InvalidBin)
            .And(r => ManualCount.AreCrossings(r.CrossingsIn, r.CrossingsOut), ValidationErrors.InvalidCrossings);

    public static ISpecification<CorrectManualCountRequest> Correct() =>
        Fx.Specification<CorrectManualCountRequest>()
            .And(r => ManualCount.AreCrossings(r.CrossingsIn, r.CrossingsOut), ValidationErrors.InvalidCrossings)
            .And(r => IsText(r.Reason, ManualCount.MaxReasonLength, required: true), ValidationErrors.InvalidReason);

    /// <summary>
    /// A tracer batch (ARV-104b): the device's clock reading, 1 to 20 runs, each with a zone, a tracer code of the fixed pattern,
    /// UTC join and exit times (exit after join, at most 3 hours) and the abandoned flag, and no run twice. Each rule names its
    /// problem without repeating a value (CWE-501).
    /// </summary>
    public static ISpecification<CaptureTracerRunsRequest> TracerRuns() =>
        Fx.Specification<CaptureTracerRunsRequest>()
            .And(r => Instant(r.DeviceClockUtc) is not null, ValidationErrors.InvalidDeviceClock)
            .And(r => r.Runs is { Count: >= 1 and <= TracerBatch.MaxRuns } && r.Runs.All(x => x is not null && x.Abandoned is not null), ValidationErrors.InvalidRuns)
            .And(r => r.Runs is null || r.Runs.All(x => x is null || x.ZoneId is { } zone && zone != Guid.Empty), ValidationErrors.ZoneNotInScope)
            .And(r => r.Runs is null || r.Runs.All(x => x is null || TracerRun.IsTracerCode(x.TracerCode)), ValidationErrors.InvalidTracerCode)
            .And(r => r.Runs is null || r.Runs.All(x => x is null || (Instant(x.JoinedUtc) is { } joined && Instant(x.ExitedUtc) is { } exited &&
                                                                      TracerRun.IsDuration(joined, exited))), ValidationErrors.InvalidRunTimes)
            .And(r => Runs(r) is not { } runs || ValidationCampaign.IsTracerBatch(runs), ValidationErrors.InvalidRuns);

    /// <summary>
    /// A desk batch (ARV-104b): a bin start on the quarter hour, 1 to 20 distinct desks with exactly 15 states each and at least
    /// one observed minute, and every state one of the four names (or null).
    /// </summary>
    public static ISpecification<CaptureDeskObservationsRequest> DeskObservations() =>
        Fx.Specification<CaptureDeskObservationsRequest>()
            .And(r => BinStart(r.BinStartUtc) is not null, ValidationErrors.InvalidBin)
            .And(r => r.Desks is null || r.Desks.All(d => d?.States is null || d.States.All(s => s is null || DeskObservation.StateOf(s) is not null)),
                ValidationErrors.InvalidState)
            .And(r => DeskMinutes(r) is { } desks && ValidationCampaign.IsDeskBatch(desks), ValidationErrors.InvalidObservations);

    public static ISpecification<CorrectDeskObservationRequest> CorrectDeskObservation() =>
        Fx.Specification<CorrectDeskObservationRequest>()
            .And(r => DeskObservation.StateOf(r.State) is not null, ValidationErrors.InvalidState)
            .And(r => IsText(r.Reason, DeskObservation.MaxReasonLength, required: true), ValidationErrors.InvalidObservationReason);

    public static ISpecification<TracerRunCriteria> TracerRunSearch() =>
        BaseCriteria.DateRangeRules<TracerRunCriteria>()
            .And(c => c.TracerCode is null || TracerRun.IsTracerCode(c.TracerCode), ValidationErrors.InvalidTracerCode)
            .And(c => c.SortBy is null || TracerRunCriteria.SortFields.Any(f => string.Equals(f, c.SortBy, StringComparison.OrdinalIgnoreCase)), ValidationErrors.InvalidSort);

    public static ISpecification<DeskObservationCriteria> DeskObservationSearch() =>
        BaseCriteria.DateRangeRules<DeskObservationCriteria>()
            .And(c => c.SortBy is null || DeskObservationCriteria.SortFields.Any(f => string.Equals(f, c.SortBy, StringComparison.OrdinalIgnoreCase)), ValidationErrors.InvalidSort);

    public static ISpecification<ValidationCampaignCriteria> Campaigns() =>
        BaseCriteria.DateRangeRules<ValidationCampaignCriteria>()
            .And(c => IsText(c.Text, 64, required: false), ValidationErrors.InvalidText)
            .And(c => c.Status is null || ValidationCampaignCriteria.Statuses.Any(s => string.Equals(s, c.Status, StringComparison.Ordinal)), ValidationErrors.InvalidStatus)
            .And(c => c.SortBy is null || ValidationCampaignCriteria.SortFields.Any(f => string.Equals(f, c.SortBy, StringComparison.OrdinalIgnoreCase)), ValidationErrors.InvalidSort);

    public static ISpecification<ManualCountCriteria> Counts() =>
        BaseCriteria.DateRangeRules<ManualCountCriteria>()
            .And(c => c.SortBy is null || ManualCountCriteria.SortFields.Any(f => string.Equals(f, c.SortBy, StringComparison.OrdinalIgnoreCase)), ValidationErrors.InvalidSort);

    /// <summary>The bin start of a request: a UTC time in ISO 8601 ending in Z on a 15-minute boundary, or null.</summary>
    public static DateTime? BinStart(string text) =>
        text is { Length: >= 11 and <= 40 } && text.EndsWith('Z') &&
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) &&
        parsed.Kind == DateTimeKind.Utc && ValidationCampaign.IsBinStart(parsed)
            ? parsed
            : null;

    /// <summary>
    /// A capture time of a request: a UTC time in ISO 8601 ending in Z (any precision), cut to the whole millisecond (the
    /// stored precision), or null.
    /// </summary>
    public static DateTime? Instant(string text) =>
        text is { Length: >= 11 and <= 40 } && text.EndsWith('Z') &&
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) &&
        parsed.Kind == DateTimeKind.Utc
            ? TracerBatch.ToMillisecond(parsed)
            : null;

    /// <summary>The runs of a tracer batch request as the domain takes them, or null when one is not complete and valid.</summary>
    public static IReadOnlyList<TracerRunInput> Runs(CaptureTracerRunsRequest request)
    {
        if (request?.Runs is null)
            return null;
        var runs = new List<TracerRunInput>(request.Runs.Count);
        foreach (var run in request.Runs)
        {
            if (run?.ZoneId is not { } zone || run.Abandoned is not { } abandoned || Instant(run.JoinedUtc) is not { } joined || Instant(run.ExitedUtc) is not { } exited)
                return null;
            runs.Add(new TracerRunInput(zone, run.TracerCode, joined, exited, abandoned));
        }

        return runs;
    }

    /// <summary>The desks of a desk batch request as the domain takes them, or null when a desk or a state is missing or not valid.</summary>
    public static IReadOnlyList<DeskMinutesInput> DeskMinutes(CaptureDeskObservationsRequest request)
    {
        if (request?.Desks is null)
            return null;
        var desks = new List<DeskMinutesInput>(request.Desks.Count);
        foreach (var desk in request.Desks)
        {
            if (desk?.DeskId is not { } id || desk.States is null)
                return null;
            var states = new List<ObservedDeskState?>(desk.States.Count);
            foreach (var text in desk.States)
            {
                if (text is null)
                {
                    states.Add(null);
                    continue;
                }

                if (DeskObservation.StateOf(text) is not { } state)
                    return null;
                states.Add(state);
            }

            desks.Add(new DeskMinutesInput(id, states));
        }

        return desks;
    }

    /// <summary>The planned days of a request, or null when one is not a local date "yyyy-MM-dd".</summary>
    public static IReadOnlyList<DateOnly> Days(IReadOnlyList<string> texts)
    {
        if (texts is null)
            return null;
        var days = new List<DateOnly>(texts.Count);
        foreach (var text in texts)
        {
            if (!ValidationCampaign.TryParseDay(text, out var day))
                return null;
            days.Add(day);
        }

        return days;
    }

    /// <summary>Free text people read: trimmed, at most <paramref name="max"/> characters, clean (DisplayText); empty only when not required.</summary>
    /// <summary>A recomputation of a closed campaign's results (ARV-104g): a reason of 1 to 500 clean characters.</summary>
    public static ISpecification<RecomputeValidationResultsRequest> Recompute() =>
        Fx.Specification<RecomputeValidationResultsRequest>()
            .And(r => IsText(r.Reason, ValidationResultsViewModel.MaxReasonLength, required: true), ValidationResultsErrors.InvalidReason);

    public static bool IsText(string text, int max, bool required) =>
        string.IsNullOrWhiteSpace(text) ? !required && (text is null || text.Length <= max) : text.Trim().Length <= max && DisplayText.IsClean(text.Trim());
}
