using System.Globalization;

namespace Ariva.Core.Domain.Criteria;

/// <summary>
/// Which bins of a queue zone's health checks to read (ARV-114a): the zone's name in the site's zone profile and a UTC
/// range of bin starts [FromDate, ToDate), both given, at most <see cref="MaxRange"/> (the range a replay or a
/// recomputation covers, SensingReplayQuery.MaxRange). Built from the query string by <see cref="Of"/>, so that a value
/// that is not a UTC time never reaches model binding (whose error would quote it back, CWE-501).
/// </summary>
public sealed record ZoneHealthCriteria : BaseCriteria
{
    /// <summary>The longest range one request reads: 31 days, 2,976 bins of 15 minutes.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    /// <summary>The longest zone name a profile allows.</summary>
    public const int MaxZoneLength = 200;

    public const string InvalidZone = "The zone is the name of a queue zone of the site, 1 to 200 characters.";
    public const string InvalidRange = "The range is from and to in UTC (ISO 8601 ending in Z), from before to, at most 31 days apart.";

    public string Zone { get; init; }

    /// <summary>The criteria from query string values: a time that is not ISO 8601 UTC ending in Z stays unset (and is refused).</summary>
    public static ZoneHealthCriteria Of(string zone, string from, string to) => new() { Zone = zone, FromDate = Utc(from), ToDate = Utc(to) };

    private static DateTime? Utc(string value) =>
        value is { Length: >= 11 and <= 40 } && value.EndsWith('Z') &&
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) &&
        parsed.Kind == DateTimeKind.Utc
            ? parsed
            : null;

    /// <summary>The rules (Fx.Specification): a zone name without control characters, and a closed UTC range of at most <see cref="MaxRange"/>.</summary>
    public static ISpecification<ZoneHealthCriteria> Rules() =>
        Fx.Specification<ZoneHealthCriteria>()
            .And(x => x.Zone is { Length: > 0 and <= MaxZoneLength } && x.Zone.Trim().Length == x.Zone.Length && !x.Zone.Any(char.IsControl), InvalidZone)
            .And(x => x.FromDate is { Kind: DateTimeKind.Utc } && x.ToDate is { Kind: DateTimeKind.Utc }, InvalidRange)
            .And(x => x.FromDate is not { } from || x.ToDate is not { } to || (from < to && to - from <= MaxRange), InvalidRange);
}
