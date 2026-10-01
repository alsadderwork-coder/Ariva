using Ariva.Api.Common.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// The security baseline every Ariva host applies (see docs/security/cwe-controls.md). Services are registered in
/// one call so no host can miss a control; the middlewares are called one by one in Program.cs because their
/// order interleaves with the AMAN middlewares that later stories add (request tracking, session context):
/// <code>
/// app.UseAppForwardedHeaders();
/// app.UseAppSecurityHeaders();
/// app.UseErrorHandling();
/// app.UseRouting();
/// app.UseAppCors();
/// app.UseAppRateLimiting();
/// app.UseAuthentication();
/// app.UsePendingScope();
/// app.UseAuthorization();
/// </code>
/// </summary>
public static class SecurityBaselineExtensions
{
    #region Services

    /// <summary>
    /// Registers default deny authentication and authorization, input limits, rate limiting, ProblemDetails error
    /// handling, trusted forwarded headers and the CORS allow-list.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration (reads the <c>Security</c> section).</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppSecurityBaseline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services
            .AddAppSecurity(configuration)
            .AddAppRequestLimits(configuration)
            .AddAppRateLimiting(configuration)
            .AddAppErrorHandling()
            .AddAppForwardedHeaders(configuration)
            .AddAppCors(configuration);
    }

    #endregion

    #region Middlewares

    /// <summary>
    /// Adds <see cref="SecurityHeadersMiddleware"/>. Call it right after <c>UseAppForwardedHeaders</c>, so error,
    /// challenge and rate limit answers carry the headers too.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseAppSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    #endregion
}
