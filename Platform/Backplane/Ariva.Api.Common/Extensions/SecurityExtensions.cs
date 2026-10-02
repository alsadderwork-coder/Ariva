using Ariva.Api.Common.Security;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// ARV-010a adds the JWT bearer scheme as the authenticate scheme; Deny still answers every challenge and forbid.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = ArivaAuthenticationSchemes.Deny;
                options.DefaultChallengeScheme = ArivaAuthenticationSchemes.Deny;
                options.DefaultForbidScheme = ArivaAuthenticationSchemes.Deny;
            })
            .AddScheme<AuthenticationSchemeOptions, DenyAuthenticationHandler>(ArivaAuthenticationSchemes.Deny, displayName: null, configureOptions: null)
            // ES256 access tokens from Ariva.Api.Main (ADR-0026, ARV-010a). Deny stays the challenge and forbid scheme,
            // so a missing or invalid token gets the same problem response as before and says nothing about why.
            .AddArivaJwtBearer()
            // Devices (ARV-022): their own scheme and policies, used only by [DeviceAuthenticated] endpoints.
            .AddArivaDeviceAuthentication();

        services.AddArivaAccounts(configuration);
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Scoped<ICurrentUser, HttpCurrentUser>());

        var authenticatedUser = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(authenticatedUser)
            .SetFallbackPolicy(authenticatedUser);

        // [Permission] policies (ARV-009): built on demand, satisfied by the user's granted permissions.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Step-up for critical actions (ARV-010d): [RequiresRecentMfa] and its RFC 9470 401 answer.
        services.AddSingleton<IAuthorizationHandler, RecentMfaHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, StepUpResultHandler>();

        return services;
    }

    #endregion
}
