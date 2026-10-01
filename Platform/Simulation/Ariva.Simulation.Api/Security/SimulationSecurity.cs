using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Ariva.Simulation.Api.Security;

/// <summary>
/// Security defaults for the simulator. The simulator references Ariva.Business.Contracts only (it plays AMAN,
/// AODB and the sensors, so it must not share Ariva's internals), therefore it carries this small copy of the
/// baseline in Ariva.Api.Common instead of referencing it: default deny with the <see cref="DenyScheme"/>
/// placeholder scheme (CWE-862, CWE-306), no <c>Server</c> header, a 1 MB body limit, ProblemDetails errors
/// without exception details and the API security headers.
/// </summary>
internal static class SimulationSecurity
{
    #region Constants

    /// <summary>Placeholder scheme that never authenticates, so everything except the probes answers 401.</summary>
    public const string DenyScheme = "Ariva.Deny";

    /// <summary>Environment in which the simulator must never run.</summary>
    public const string ProductionEnvironment = "k8s-prd";

    private const long MaxRequestBodyBytes = 1_048_576;

    #endregion

    #region Services

    /// <summary>Registers default deny, the body limit and ProblemDetails.</summary>
    /// <param name="services">The simulator's service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddSimulationSecurity(this IServiceCollection services)
    {
        services
            .AddAuthentication(DenyScheme)
            .AddScheme<AuthenticationSchemeOptions, DenyAuthenticationHandler>(DenyScheme, displayName: null, configureOptions: null);

        var authenticatedUser = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(authenticatedUser)
            .SetFallbackPolicy(authenticatedUser);

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

        return app;
    }

    #endregion

    #region Handler

    /// <summary>Ignores every credential and returns no result, so the fallback policy challenges with 401.</summary>
    /// <param name="options">The scheme options.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    private sealed class DenyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }
    }

    #endregion
}
