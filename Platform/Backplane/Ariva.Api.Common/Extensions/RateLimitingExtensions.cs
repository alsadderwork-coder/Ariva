using System.Globalization;
using System.Threading.RateLimiting;
using Ariva.Api.Common.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Rate limiting with the built in <c>Microsoft.AspNetCore.RateLimiting</c> middleware, read from
/// <c>Security:RateLimiting</c>.
/// </summary>
public static class RateLimitingExtensions
{
    #region Constants

    /// <summary>
    /// Named policy for login, token refresh and TOTP endpoints. Apply it with
    /// <c>[EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]</c> or <c>RequireRateLimiting(AuthPolicy)</c>.
    /// </summary>
    public const string AuthPolicy = "auth";

    /// <summary>
    /// Named policy for large uploads (floor plans, ARV-018): one at a time per host process and two waiting, so
    /// concurrent 20 MB bodies cannot exhaust memory or the temporary volume (CWE-400). The rest get 429.
    /// </summary>
    public const string UploadPolicy = "upload";

    /// <summary>
    /// Named policy for alert rule backtests (ARV-038): each reads up to a day of minutes and bins of up to 64 zones, so
    /// two run at a time per host process and two wait; the rest get 429 (CWE-400, CWE-770).
    /// </summary>
    public const string BacktestPolicy = "backtest";

    /// <summary>
    /// Named policy for device endpoints (ARV-022): a fixed window per device, keyed by the presented credential's
    /// prefix before any database work, so one chatty or broken sensor cannot starve the others behind the same address.
    /// </summary>
    public const string DevicePolicy = "device";

    private const string UnknownClient = "unknown";

    #endregion

    #region Services

    /// <summary>
    /// Registers a global limiter partitioned per client IP address and the stricter <see cref="AuthPolicy"/>
    /// policy. Rejected requests get 429 with <c>Retry-After</c>; the status code pages middleware writes the
    /// ProblemDetails body.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RateLimitingSettings>()
            .Bind(configuration.GetSection(RateLimitingSettings.SectionName))
            .Validate(settings => settings.Global.IsValid && settings.Auth.IsValid && settings.Device.IsValid,
                $"{RateLimitingSettings.SectionName} limits need a positive PermitLimit and WindowSeconds and a QueueLimit of zero or more.")
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });

        services
            .AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingSettings>>((options, settings) =>
            {
                var limits = settings.Value;
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => ToLimiterOptions(limits.Global)));
                options.AddPolicy(AuthPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => ToLimiterOptions(limits.Auth)));
                options.AddPolicy(DevicePolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(Ariva.Api.Common.Security.DeviceAuthentication.RateLimitPartition(context), _ => ToLimiterOptions(limits.Device)));
                options.AddPolicy(UploadPolicy, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(UploadPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 1,
                        QueueLimit = 2,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
                options.AddPolicy(BacktestPolicy, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(BacktestPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 2,
                        QueueLimit = 2,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
                options.OnRejected = (context, _) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    return ValueTask.CompletedTask;
                };
            });

        return services;
    }

    #endregion

    #region Middlewares

    /// <summary>
    /// Adds the rate limiting middleware. Call it after <c>UseRouting</c> and <c>UseAppCors</c> (so endpoint
    /// policies apply and 429 answers carry CORS headers) and before <c>UseAuthentication</c> (so floods are
    /// rejected before any token work).
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseAppRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseRateLimiter();
    }

    #endregion

    #region Helpers

    private static string ClientKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return UnknownClient;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static FixedWindowRateLimiterOptions ToLimiterOptions(FixedWindowSettings window) => new()
    {
        PermitLimit = window.PermitLimit,
        Window = TimeSpan.FromSeconds(window.WindowSeconds),
        QueueLimit = window.QueueLimit,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true
    };

    #endregion
}
