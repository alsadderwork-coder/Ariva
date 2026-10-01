using System.Security.Claims;
using Ariva.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Resolves what a signed-in user may do. Until server-side sessions land (ARV-010b) the roles come from the
/// principal's role claims and the grants from <see cref="RolePermissions"/>; ARV-010b replaces this with the session's
/// roles and ARV-011 with the stored grants, behind the same interface.
/// </summary>
public interface IPermissionResolver
{
    Task<IReadOnlySet<Permission>> GetPermissionsAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class RoleClaimPermissionResolver : IPermissionResolver
{
    public Task<IReadOnlySet<Permission>> GetPermissionsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return Task.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>());

        var roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value);
        return Task.FromResult(RolePermissions.For(roles));
    }
}

/// <summary>Succeeds when the user holds any permission of the requirement (OR, AMAN semantics).</summary>
public sealed class PermissionAuthorizationHandler(IPermissionResolver resolver) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var granted = await resolver.GetPermissionsAsync(context.User);
        if (requirement.Permissions.Any(granted.Contains))
            context.Succeed(requirement);
    }
}
