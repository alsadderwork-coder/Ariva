using System.Security.Claims;

namespace Ariva.Core.Security;

/// <summary>
/// What a signed-in user may do (ARV-009). The stored implementation reads the user's role grants (ARV-010a; cached,
/// evicted by tag when grants change); ARV-010b moves the lookup behind the server-side session.
/// </summary>
public interface IPermissionResolver
{
    Task<IReadOnlySet<Permission>> GetPermissionsAsync(ClaimsPrincipal user, CancellationToken ct = default);
}
