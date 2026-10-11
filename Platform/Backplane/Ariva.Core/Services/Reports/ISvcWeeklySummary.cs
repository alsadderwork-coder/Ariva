using Ariva.Core.Reports;

namespace Ariva.Core.Services.Reports;

public static class WeeklySummaryErrors
{
    /// <summary>A site the caller does not reach, that does not exist, or whose reports the caller may not read: one answer (CWE-204).</summary>
    public const string NotFound = "The site does not exist.";

    public const string InvalidWeek = "The week is the date of its Monday (YYYY-MM-DD), not after the current week at the site and at most 57 weeks back.";

    /// <summary>The site's airport names a time zone this host does not know: never summarised in UTC instead.</summary>
    public const string TimeZoneUnknown = "The site's time zone is not known on this host; correct the airport's time zone.";

    /// <summary>A read passed its bound (<see cref="WeeklySummaryLimits"/>): refused, never cut short (CWE-120).</summary>
    public const string TooLarge = "The week holds more than a summary reads (lanes, desks, alerts, devices or outages); it is refused rather than cut short.";
}

/// <summary>
/// The weekly summary of a site (ARV-119, <see cref="WeeklySummary"/>): figures only, per site and local week, for the
/// report tool of ARV-146 (no endpoint, template, document or delivery here). For the caller's sites only (ISiteScope),
/// and only for a caller whose roles read reports (<c>Report.View</c>); a site outside either answers like one that does
/// not exist, before the week is checked (a pending or disabled account holds no role). Projected for the caller's roles:
/// the alerts its roles see, per-desk rows only with <c>BorderDesks.View</c> (without it, each border lane's day and week only
/// where at least three desks were open in it, the days all or none), airport desks' lanes only with
/// <c>AirportDesks.View</c>, data quality and availability only with <c>DataQuality.View</c>.
/// </summary>
public interface ISvcWeeklySummary : ISvcScoped
{
    /// <summary>The summary of the local week starting on <paramref name="weekStart"/> (a Monday); a null week is refused after the site is checked.</summary>
    Task<Result<WeeklySummary>> GetAsync(string siteCode, DateOnly? weekStart, CancellationToken ct = default);
}
