using Ariva.Core;
using Ariva.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>A user needs any one of these permissions.</summary>
public sealed class PermissionRequirement(IReadOnlyList<Permission> permissions) : IAuthorizationRequirement
{
    public IReadOnlyList<Permission> Permissions { get; } = permissions;
}

/// <summary>
/// Builds "Permission:ViewDesk,EditDesk" policies on demand: an authenticated user holding any of the permissions.
/// "RecentMfa:&lt;seconds&gt;" policies come from <see cref="RequiresRecentMfaAttribute"/> (ARV-010d). Other policy
/// names go to the default provider. An unknown permission name fails closed: the policy can never be
/// satisfied and the error is logged (a unit test also checks every [Permission] name at build time).
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options, ILogger<PermissionPolicyProvider> logger) : IAuthorizationPolicyProvider
{
    public const string Prefix = "Permission:";

    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public static string PolicyFor(IEnumerable<string> permissionNames) => Prefix + string.Join(',', permissionNames);

    public Task<AuthorizationPolicy> GetPolicyAsync(string policyName)
    {
        if (RecentMfaRequirement.PolicyFor(policyName) is { } recentMfa)
            return Task.FromResult(recentMfa);
        if (policyName is null || !policyName.StartsWith(Prefix, StringComparison.Ordinal))
            return _fallback.GetPolicyAsync(policyName);

        var names = policyName[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var permissions = names.Select(Global.Defaults.Permissions.Find).ToList();
        if (names.Length == 0 || permissions.Any(p => p is null))
        {
            logger.LogError("Policy {Policy} names an unknown permission; access is denied", policyName);
            return Task.FromResult(new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build());
        }

        return Task.FromResult(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permissions))
            .Build());
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}
