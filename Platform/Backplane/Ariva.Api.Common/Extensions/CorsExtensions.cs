using Ariva.Api.Common.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Cross origin access for the Ariva web app, read from <c>Security:Cors</c>.
/// </summary>
public static class CorsExtensions
{
    #region Constants

    /// <summary>Name of the CORS policy applied to every endpoint.</summary>
    public const string PolicyName = "ariva-web";

    private static readonly string[] AllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    private static readonly string[] AllowedHeaders =
        ["Authorization", "Content-Type", "Accept", "Accept-Language", "X-Requested-With", "X-SignalR-User-Agent"];

    #endregion

    #region Services

    /// <summary>
    /// Registers the <see cref="PolicyName"/> policy from the configured allow-list of exact origins. Wildcards are
    /// rejected at startup, so <c>AllowAnyOrigin</c> is never combined with credentials; an empty list allows no
    /// cross origin caller.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .Validate(settings => settings.IsValid,
                $"{CorsSettings.SectionName}:AllowedOrigins must list exact http or https origins (no wildcard, no path, no trailing slash).")
            .ValidateOnStart();

        services.AddCors();

        services
            .AddOptions<CorsOptions>()
            .Configure<IOptions<CorsSettings>>((options, settings) =>
            {
                var cors = settings.Value;
                options.AddPolicy(PolicyName, policy =>
                {
                    policy
                        .WithOrigins(cors.AllowedOrigins ?? [])
                        .WithMethods(AllowedMethods)
                        .WithHeaders(AllowedHeaders)
                        .SetPreflightMaxAge(TimeSpan.FromSeconds(cors.PreflightMaxAgeSeconds));

                    if (cors.AllowCredentials)
                    {
                        policy.AllowCredentials();
                    }
                    else
                    {
                        policy.DisallowCredentials();
                    }
                });
            });

        return services;
    }

    #endregion

    #region Middlewares

    /// <summary>
    /// Applies the <see cref="PolicyName"/> policy. Call it after <c>UseRouting</c> and before authentication, so
    /// preflight requests are answered without credentials.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseAppCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseCors(PolicyName);
    }

    #endregion
}
