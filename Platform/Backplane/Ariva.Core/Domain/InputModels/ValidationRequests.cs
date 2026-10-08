using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// A validation campaign to plan (ARV-104a). The site comes from the route, never the body. <c>ProfileVersion</c> names the
/// site's published zone profile version the creator reviewed (a newer one answers 409); zones and lines are ids of that
/// version (from GET api/v1/admin/zone-profiles/{id}); days are local dates "yyyy-MM-dd" in the site's time zone. A target
/// left empty takes its placeholder default until TC-04 is answered.
/// </summary>
public sealed record CreateValidationCampaignRequest(
    [MaxLength(ValidationCampaign.MaxNameLength)] string Name,
    int? ProfileVersion,
    [MaxLength(ValidationCampaign.MaxZones)] IReadOnlyList<Guid> ZoneIds,
    [MaxLength(ValidationCampaign.MaxLines)] IReadOnlyList<Guid> LineIds,
    [MaxLength(ValidationCampaign.MaxDays)] IReadOnlyList<string> Days,
    int? TargetBinsPerLine,
    int? TargetTracerRuns);

/// <summary>
/// One observer's count of a line for one 15-minute bin (ARV-104a): the bin's start in UTC on the quarter hour ("2026-10-08T06:15:00Z"),
/// and the people tallied crossing the line in and out. The observer is the caller.
/// </summary>
public sealed record CaptureManualCountRequest(
    Guid? LineId,
    [MaxLength(40)] string BinStartUtc,
    int? CrossingsIn,
    int? CrossingsOut);

/// <summary>A correction of one's own count (ARV-104a): the corrected crossings and why. It becomes the next revision; the corrected one stays.</summary>
public sealed record CorrectManualCountRequest(
    int? CrossingsIn,
    int? CrossingsOut,
    [MaxLength(ManualCount.MaxReasonLength)] string Reason);
