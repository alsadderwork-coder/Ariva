using System.Security.Claims;
using Ariva.Core.Security;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Security;

/// <summary>
/// Permissions from the user's stored role grants (ARV-010a) through <see cref="RolePermissions"/>. Cached for one
/// minute per user with the tag "user:{id}"; ARV-011 evicts the tag when grants change. A pending account (temporary
/// password, unfinished TOTP) and a disabled account hold nothing. Principals without a subject (test authentication)
/// are resolved from their role claims.
/// </summary>
internal sealed class StoredPermissionResolver(IUnitOfWork unitOfWork, IFusionCache cache) : IPermissionResolver
{
    public static string Tag(Guid userId) => $"user:{userId:N}";

    public async Task<IReadOnlySet<Permission>> GetPermissionsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user?.Identity?.IsAuthenticated != true || user.FindFirst(ArivaClaims.Scope)?.Value == ArivaClaims.PendingScope)
            return new HashSet<Permission>();

        // Ariva access tokens always carry "sub" and never roles: their grants are read from the database, so a
        // change of roles applies within the cache minute instead of at token expiry. Only principals without a
        // subject (the test authentication scheme) are resolved from role claims.
        var subject = user.FindFirst(ArivaClaims.Subject)?.Value;
        if (subject is null)
            return RolePermissions.For(user.FindAll(ClaimTypes.Role).Select(c => c.Value));

        if (!Guid.TryParse(subject, out var userId))
            return new HashSet<Permission>();

        var roles = await cache.GetOrSetAsync<string[]>(
            $"roles:{userId:N}",
            async (_, token) => (await unitOfWork.StorageProvider.Query<UserRole>()
                .Where(r => r.User.Id == userId && !r.User.IsDisabled)
                .Select(r => r.RoleCode)
                .ToListAsync(token)).ToArray(),
            options => SecurityCacheOptions.Apply(options, TimeSpan.FromMinutes(1)),
            tags: [Tag(userId)],
            token: ct);

        return RolePermissions.For(roles);
    }
}
