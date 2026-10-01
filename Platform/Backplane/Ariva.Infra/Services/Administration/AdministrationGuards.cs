using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Administration;

/// <summary>
/// Rules every administrative action on an account shares (ARV-011, ARV-012):
/// <list type="bullet">
/// <item>The caller administers only accounts whose site access it covers; anything else answers like an unknown
/// account, so a site-limited administrator can neither see nor take over a wider account (CWE-863, CWE-269).</item>
/// <item>The last active administrator (break-glass aside) keeps the role and stays enabled. The check runs under a
/// transaction-scoped advisory lock, so two administrators removing each other at once cannot both succeed
/// (CWE-362).</item>
/// </list>
/// </summary>
internal sealed class AdministrationGuards(IUnitOfWork unitOfWork, ISiteScope siteScope) : SvcDb(unitOfWork)
{
    // Any constant shared by every path that can remove an administrator.
    private const long AdministratorsLock = 0x41524956_00000011;

    public static SiteAccess AccessOf(User user) =>
        new(user.AllSites, user.Sites.Select(s => s.SiteCode).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// True when the caller's sites cover everything the target account can reach. An administrator without sites yet
    /// (just created) is covered only by an all-sites caller, so a site-limited administrator cannot take it over
    /// before its sites are set.
    /// </summary>
    public async Task<bool> CoversAsync(User target, CancellationToken ct = default)
    {
        var caller = await siteScope.GetAsync(ct);
        if (!caller.AllSites && !target.AllSites && target.Sites.Count == 0 && target.Holds(RoleCodes.SystemAdministrator))
            return false;
        return caller.Covers(AccessOf(target));
    }

    /// <summary>The accounts the caller may administer: all for an all-sites caller, otherwise those inside its sites.</summary>
    public static IQueryable<User> Administrable(IQueryable<User> users, SiteAccess caller)
    {
        if (caller.AllSites)
            return users;
        var codes = caller.SiteCodes.ToList();
        return users.Where(u => !u.AllSites && !u.Sites.Any(s => !codes.Contains(s.SiteCode)) &&
                                (u.Sites.Any() || !u.Roles.Any(r => r.RoleCode == RoleCodes.SystemAdministrator)));
    }

    /// <summary>
    /// True when <paramref name="userId"/> is the only active, regular SystemAdministrator. Takes the administrators
    /// lock until the transaction ends, so call it right before the change it guards.
    /// </summary>
    public async Task<bool> IsLastAdministratorAsync(Guid userId, CancellationToken ct = default)
    {
        await ExecuteCommandAsync<LockRow>(
            """SELECT 1 AS "Value" FROM pg_advisory_xact_lock(:key)""",
            new Dictionary<string, object> { ["key"] = AdministratorsLock },
            ct);
        var others = await Query<UserRole>().CountAsync(
            r => r.RoleCode == RoleCodes.SystemAdministrator && r.User.Id != userId && !r.User.IsDisabled && !r.User.IsBreakGlass, ct);
        return others == 0;
    }

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
