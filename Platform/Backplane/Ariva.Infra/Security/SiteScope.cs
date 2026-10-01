using Ariva.Core.Security;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Security;

/// <summary>
/// The caller's sites from the stored bindings (ARV-012), cached for one minute per user with the tag "user:{id}"
/// that every change of roles, sites or account state evicts. A caller without an id (anonymous, system jobs without
/// a user) and a disabled account have no site access.
/// </summary>
internal sealed class SiteScope(IUnitOfWork unitOfWork, ICurrentUser currentUser, IFusionCache cache) : ISiteScope
{
    private SiteAccess _resolved;

    public async Task<SiteAccess> GetAsync(CancellationToken ct = default)
    {
        if (_resolved is not null)
            return _resolved;
        if (currentUser.Id is not { } userId)
            return _resolved = SiteAccess.None;

        var stored = await cache.GetOrSetAsync<StoredAccess>(
            $"sites:{userId:N}",
            async (_, token) =>
            {
                var user = await unitOfWork.StorageProvider.Query<User>()
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.AllSites, u.IsDisabled })
                    .FirstOrDefaultAsync(token);
                if (user is null || user.IsDisabled)
                    return new StoredAccess(false, []);
                var codes = await unitOfWork.StorageProvider.Query<UserSite>()
                    .Where(s => s.User.Id == userId)
                    .Select(s => s.SiteCode)
                    .ToListAsync(token);
                return new StoredAccess(user.AllSites, [.. codes]);
            },
            options => options.SetDuration(TimeSpan.FromMinutes(1)),
            tags: [StoredPermissionResolver.Tag(userId)],
            token: ct);

        return _resolved = new SiteAccess(stored.AllSites, stored.SiteCodes.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The cached form (a plain record the distributed cache can serialise).</summary>
    public sealed record StoredAccess(bool AllSites, string[] SiteCodes);
}
