using Ariva.Core.Availability;

namespace Ariva.Core.Services.Quality;

public static class AvailabilityErrors
{
    /// <summary>A site the caller does not reach, or that does not exist: one answer (CWE-204).</summary>
    public const string NotFound = "The site does not exist.";
}

/// <summary>
/// A site's availability over local days (ARV-118, formulas F18): per day, per week (Monday to Sunday; a week cut by the
/// range holds fewer days) and over the range, the ledger's minute counts and available operating minutes over operating
/// minutes (null without operating minutes). Days are the site's local dates as the ledger recorded them.
/// </summary>
public sealed record AvailabilityViewModel(
    string SiteCode,
    string TimeZoneId,
    DateOnly From,
    DateOnly To,
    double PilotTarget,
    AvailabilityCounts Total,
    IReadOnlyList<AvailabilityWeek> Weeks,
    IReadOnlyList<AvailabilityDay> Days);

/// <summary>
/// The availability ledger read (ARV-118): for the caller's sites only (ISiteScope); a site the caller does not reach
/// answers like one that does not exist, before the range is checked.
/// </summary>
public interface ISvcAvailability : ISvcScoped
{
    Task<Result<AvailabilityViewModel>> GetAsync(string siteCode, AvailabilityCriteria criteria, CancellationToken ct = default);
}
