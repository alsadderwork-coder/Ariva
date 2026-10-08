using System.Globalization;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.InputModels;

namespace Ariva.Core.Services.Validation;

/// <summary>
/// The request rules of the validation campaign and manual count API (ARV-104a, Fx.Specification), checked before any entity
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
            .And(r => ValidationCampaign.AreValidTargets(r.TargetBinsPerLine, r.TargetTracerRuns), ValidationErrors.InvalidTargets);

    public static ISpecification<CaptureManualCountRequest> Capture() =>
        Fx.Specification<CaptureManualCountRequest>()
            .And(r => r.LineId is { } id && id != Guid.Empty, ValidationErrors.LineNotInScope)
            .And(r => BinStart(r.BinStartUtc) is not null, ValidationErrors.InvalidBin)
            .And(r => ManualCount.AreCrossings(r.CrossingsIn, r.CrossingsOut), ValidationErrors.InvalidCrossings);

    public static ISpecification<CorrectManualCountRequest> Correct() =>
        Fx.Specification<CorrectManualCountRequest>()
            .And(r => ManualCount.AreCrossings(r.CrossingsIn, r.CrossingsOut), ValidationErrors.InvalidCrossings)
            .And(r => IsText(r.Reason, ManualCount.MaxReasonLength, required: true), ValidationErrors.InvalidReason);

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
    public static bool IsText(string text, int max, bool required) =>
        string.IsNullOrWhiteSpace(text) ? !required && (text is null || text.Length <= max) : text.Trim().Length <= max && DisplayText.IsClean(text.Trim());
}
