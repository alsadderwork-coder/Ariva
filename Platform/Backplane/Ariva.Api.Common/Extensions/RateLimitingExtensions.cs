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

    /// <summary>Named policy for display players (ARV-058): per presented credential, else per address, at the device rate.</summary>
    public const string DisplayPolicy = "display";

    /// <summary>Named policy for the integration token exchange (ARV-042): a fixed window per client address.</summary>
    public const string IntegrationAuthPolicy = "integration-auth";

    /// <summary>
    /// Named policy for the Integration API's batch endpoints (ARV-043): a concurrency limit per host process, applied
    /// before authentication and before any body is buffered, so a burst of 1 MB batches cannot exhaust memory or the
    /// temporary volume (CWE-400). The rest get 429; integrators retry with the same Idempotency-Key.
    /// </summary>
    public const string IntegrationBatchPolicy = "integration-batch";

    /// <summary>Named policy for AIDX messages (ARV-044): like <see cref="IntegrationBatchPolicy"/>, for bodies of up to 5 MB.</summary>
    public const string IntegrationAidxPolicy = "integration-aidx";

    /// <summary>
    /// Named policy for the validation results (ARV-104g): a concurrency limit per host process, applied before authentication.
    /// A request may wait for a computation (one at a time per host, up to the results' request timeout), so only a few run at
    /// once and a few more wait for a turn (callers may wait, oldest first); the rest get 429 with Retry-After (CWE-400, CWE-770).
    /// </summary>
    public const string ValidationResultsPolicy = "validation-results";

    /// <summary>The Retry-After of a 429 from a concurrency policy, whose lease names no time (uploads, batches, backtests, validation results).</summary>
    public const int ConcurrencyRetryAfterSeconds = 10;

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
            .Validate(settings => settings.Global.IsValid && settings.Auth.IsValid && settings.Device.IsValid && settings.IntegrationAuth.IsValid &&
                                settings.IntegrationBatch.IsValid && settings.IntegrationAidx.IsValid && settings.IntegrationClient.IsValid &&
                                settings.ValidationResults.IsValid,
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
                options.AddPolicy(IntegrationAuthPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => ToLimiterOptions(limits.IntegrationAuth)));
                options.AddPolicy(DevicePolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(Ariva.Api.Common.Security.DeviceAuthentication.RateLimitPartition(context), _ => ToLimiterOptions(limits.Device)));
                options.AddPolicy(DisplayPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(Ariva.Api.Common.Security.DisplayAuthentication.RateLimitPartition(context), _ => ToLimiterOptions(limits.Device)));
                options.AddPolicy(UploadPolicy, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(UploadPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 1,
                        QueueLimit = 2,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
                options.AddPolicy(IntegrationBatchPolicy, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(IntegrationBatchPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = limits.IntegrationBatch.PermitLimit,
                        QueueLimit = limits.IntegrationBatch.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
                options.AddPolicy(IntegrationAidxPolicy, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(IntegrationAidxPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = limits.IntegrationAidx.PermitLimit,
                        QueueLimit = limits.IntegrationAidx.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
                options.AddPolicy(ValidationResultsPolicy, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(ValidationResultsPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = limits.ValidationResults.PermitLimit,
                        QueueLimit = limits.ValidationResults.QueueLimit,
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
                    // A window limiter's lease says when the window reopens; a concurrency limiter's lease carries no such
                    // metadata (a permit frees when a request ends), so its 429 says to retry after a fixed delay instead
                    // (first security review of ARV-104g, M1: the validation-results 429 had no Retry-After).
                    var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                        ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                        : ConcurrencyRetryAfterSeconds;
                    context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

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
