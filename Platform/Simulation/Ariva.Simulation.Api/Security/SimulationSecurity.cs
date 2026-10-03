using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Ariva.Simulation.Api.Security;

/// <summary>
/// Security defaults for the simulator. The simulator references Ariva.Business.Contracts only (it plays AMAN,
/// AODB and the sensors, so it must not share Ariva's internals), therefore it carries this small copy of the
/// baseline in Ariva.Api.Common instead of referencing it: default deny (CWE-862, CWE-306) with the operator key
/// scheme <see cref="KeyScheme"/> (ARV-027; no key configured means nothing authenticates), the read and control
/// policies, a per-key limit on scenario re-runs, no <c>Server</c> header, a 1 MB body limit, ProblemDetails errors
/// without exception details and the API security headers.
/// </summary>
internal static class SimulationSecurity
{
    #region Constants

    /// <summary>The operator key scheme (<see cref="SimulationKeyHandler"/>).</summary>
    public const string KeyScheme = "Ariva.SimulationKey";

    /// <summary>Environment in which the simulator must never run.</summary>
    public const string ProductionEnvironment = "k8s-prd";

    private const long MaxRequestBodyBytes = 1_048_576;

    #endregion

    #region Services

    /// <summary>Registers default deny with operator keys, the policies, the re-run limit, the body limit and ProblemDetails.</summary>
    /// <param name="services">The simulator's service collection.</param>
    /// <param name="configuration">The simulator's configuration (Simulation:Control).</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddSimulationSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<SimulationControlSettings>()
            .Bind(configuration.GetSection(SimulationControlSettings.Section))
            .ValidateDataAnnotations() // runs SimulationControlSettings.Validate (IValidatableObject)
            .ValidateOnStart();

        services
            .AddAuthentication(KeyScheme)
            .AddScheme<AuthenticationSchemeOptions, SimulationKeyHandler>(KeyScheme, displayName: null, configureOptions: null);

        var authenticatedUser = new AuthorizationPolicyBuilder(KeyScheme).RequireAuthenticatedUser().Build();
        services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(authenticatedUser)
            .SetFallbackPolicy(authenticatedUser)
            .AddPolicy(SimulationScopes.ReadPolicy, policy => policy
                .AddAuthenticationSchemes(KeyScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(SimulationScopes.Claim, SimulationScopes.Read, SimulationScopes.Control))
            .AddPolicy(SimulationScopes.ControlPolicy, policy => policy
                .AddAuthenticationSchemes(KeyScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(SimulationScopes.Claim, SimulationScopes.Control));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(SimulationScopes.PlayLimit, context => RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(SimulationScopes.RerunLimit, context =>
            {
                var perMinute = context.RequestServices.GetRequiredService<IOptionsMonitor<SimulationControlSettings>>().CurrentValue.RerunsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? "anonymous", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = perMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
            });
        });

        services.Configure<KestrelServerOptions>(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Detail = context.Exception is null ? context.ProblemDetails.Detail : null;
            context.ProblemDetails.Extensions.Remove("exception");
        });

        return services;
    }

    #endregion

    #region Middlewares

    /// <summary>Adds the security headers, error handling, routing, authentication and authorization.</summary>
    /// <param name="app">The simulator application.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication UseSimulationSecurity(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
                headers.Remove(HeaderNames.Server);
                if (context.Request.Headers.ContainsKey(HeaderNames.Authorization))
                {
                    headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        return app;
    }

    #endregion
}
