using Ariva.Core;
using Ariva.Core.Reports;
using Ariva.Core.Security;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Reports;

/// <summary>
/// The daily report on demand (ARV-060, <see cref="ISvcReports"/>): a site the caller reaches, a local day at most
/// 400 days back and not after today at the site; the alerts listed are those the caller's roles see.
/// </summary>
internal sealed class SvcReports(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, CallerRoles callerRoles, ReportReader reader)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcReports
{
    public async Task<Result<DailyReport>> DailyAsync(string siteCode, DateOnly? date, CancellationToken ct = default)
    {
        // A site outside the caller's own answers like one that does not exist (CWE-204).
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) || !await reader.SiteExistsAsync(siteCode, ct))
            return Result.Error<DailyReport>(ReportErrors.NotFound);
        var now = UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, await reader.TimeZoneAsync(siteCode, ct)));
        if (date is not { } day || day > today || day < today.AddDays(-ISvcReports.MaxDaysBack))
            return Result.Error<DailyReport>(ReportErrors.InvalidDate);
        return new Result<DailyReport>(await reader.ReadAsync(siteCode, day, await callerRoles.GetAsync(ct), now, ct));
    }
}

/// <summary>Who may receive a site's report: the roles that read reports (ARV-060).</summary>
internal static class ReportReaders
{
    public static IReadOnlyList<string> Roles { get; } =
        [.. RolePermissions.ByRole.Where(r => r.Value.Contains(Global.Defaults.Permissions.ViewReport)).Select(r => r.Key).Order(StringComparer.Ordinal)];
}
