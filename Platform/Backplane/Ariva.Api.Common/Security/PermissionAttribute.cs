using Ariva.Core;
using Microsoft.AspNetCore.Authorization;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Requires one of the named permissions (OR), AMAN's usage:
/// <c>[Permission(nameof(Global.Defaults.Permissions.ViewDesk))]</c>. It is an <see cref="AuthorizeAttribute"/> with a
/// policy name, so the endpoint carries ordinary authorization metadata (the endpoint inventory test sees it) and
/// <see cref="PermissionPolicyProvider"/> builds the policy. Controllers never name roles (CWE-863).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PermissionAttribute : AuthorizeAttribute
{
    public PermissionAttribute(params string[] permissionNames)
    {
        if (permissionNames is null || permissionNames.Length == 0 || permissionNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Name at least one permission from Global.Defaults.Permissions.", nameof(permissionNames));

        PermissionNames = permissionNames;
        Policy = PermissionPolicyProvider.PolicyFor(permissionNames);
    }

    /// <summary>Property names in <see cref="Global.Defaults.Permissions"/>.</summary>
    public IReadOnlyList<string> PermissionNames { get; }
}
