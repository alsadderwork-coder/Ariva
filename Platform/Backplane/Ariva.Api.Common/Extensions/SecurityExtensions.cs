using Ariva.Api.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Authentication and authorization defaults shared by every host: default deny (CWE-862, CWE-306).
/// </summary>
public static class SecurityExtensions
{
    #region Services

    /// <summary>
    /// Registers the <see cref="ArivaAuthenticationSchemes.Deny"/> placeholder scheme as the default scheme and an
    /// authorization fallback policy that requires an authenticated user. Every endpoint without explicit
    /// authorization metadata, and every request that matches no endpoint, is therefore challenged with 401.
    /// Health probes opt out with <c>AllowAnonymous()</c> and are listed in <c>security/allowlist.json</c>.
    /// The authentication story replaces the placeholder with the JWT bearer schemes; the fallback policy stays.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = ArivaAuthenticationSchemes.Deny;
                options.DefaultChallengeScheme = ArivaAuthenticationSchemes.Deny;
                options.DefaultForbidScheme = ArivaAuthenticationSchemes.Deny;
            })
            .AddScheme<AuthenticationSchemeOptions, DenyAuthenticationHandler>(ArivaAuthenticationSchemes.Deny, displayName: null, configureOptions: null);

        var authenticatedUser = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(authenticatedUser)
            .SetFallbackPolicy(authenticatedUser);

        // [Permission] policies (ARV-009): built on demand, satisfied by the user's granted permissions.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IPermissionResolver, RoleClaimPermissionResolver>();

        return services;
    }

    #endregion
}
