using System.Security.Claims;
using Ariva.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace Ariva.Api.Common.Security;

/// <summary>Grants from role claims only (test hosts and tools); the hosts use the stored grants.</summary>
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
