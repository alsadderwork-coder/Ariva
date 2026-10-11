using Ariva.Core.Reports;
using Ariva.Core.Security;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Settings;

namespace Ariva.Infra.Services.Reports;

/// <summary>
/// The weekly summary on demand (ARV-119, <see cref="ISvcWeeklySummary"/>): a site the caller reaches (ISiteScope) and whose
/// reports its roles read (<c>Report.View</c>), checked first and answered alike (NotFound, CWE-204, CWE-863); then the
/// site's time zone (refused when its airport's zone is unknown on this host); then the week (a Monday, not after the
/// current local week, at most 57 weeks back). The figures are read for the caller's own roles (<see cref="WeeklySummaryReader"/>).
/// </summary>
internal sealed class SvcWeeklySummary(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, CallerRoles callerRoles,
    AuthSettings authSettings, WeeklySummaryReader reader)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcWeeklySummary
{
    public async Task<Result<WeeklySummary>> GetAsync(string siteCode, DateOnly? weekStart, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) || !await reader.SiteExistsAsync(siteCode, ct))
            return Result.Error<WeeklySummary>(WeeklySummaryErrors.NotFound);
        var roles = await RolesAsync(ct);
        // No endpoint carries this yet (ARV-146 adds it with its permission): the service refuses a caller whose roles do not
        // read reports itself, so a later caller cannot reach the figures without it (fail closed).
        if (!RolePermissions.For(roles).Contains(Ariva.Core.Global.Defaults.Permissions.ViewReport))
            return Result.Error<WeeklySummary>(WeeklySummaryErrors.NotFound);

        var (known, zone) = await reader.TimeZoneAsync(siteCode, ct);
        if (!known)
            return Result.Error<WeeklySummary>(WeeklySummaryErrors.TimeZoneUnknown);
        var now = UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        if (!WeeklySummaries.IsValidWeek(weekStart, today))
            return Result.Error<WeeklySummary>(WeeklySummaryErrors.InvalidWeek);
        return await reader.ReadAsync(siteCode, weekStart.Value, zone, roles, now, ct);
    }

    /// <summary>
    /// The caller's stored roles, or none for an anonymous, disabled or pending account (a temporary password, or TOTP required
    /// and not enrolled: <see cref="User.IsPending"/>, the rule that gives a token the pending scope), as the permission
    /// resolver treats a pending principal. The service has no principal to hand the resolver, so it reads the stored account.
    /// </summary>
    private async Task<IReadOnlyList<string>> RolesAsync(CancellationToken ct)
    {
        var id = CurrentUser.Id;
        if (!id.HasValue)
            return [];
        var user = await GetAsync<User>(id.Value, ct);
        if (user is null || user.IsDisabled || user.IsPending(authSettings.TotpRequired))
            return [];
        return await callerRoles.GetAsync(ct);
    }
}
