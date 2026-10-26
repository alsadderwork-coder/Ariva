using System.Globalization;
using System.Threading.RateLimiting;
using Ariva.Api.Common.Logging;
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

    /// <summary>Named policy for display players' proof of play batches (ARV-144): per presented credential, a few a minute.</summary>
    public const string DisplayPlaysPolicy = "display-plays";

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

    /// <summary>
    /// Named policy for the on-demand daily report and its CSV files (ARV-096): a concurrency limit per host process, applied before
    /// authentication. Each request reads a site's local day of minutes, so only a few build at once and a few more wait for a turn
    /// (oldest first); the rest get 429 with Retry-After (CWE-400, CWE-770). The request timeout policy of the same name bounds each.
    /// </summary>
    public const string ReportsPolicy = "reports";

    /// <summary>
    /// The concurrency policies (ARV-096b): a request under one holds a permit of its host while it runs, so each body it binds is read
    /// steadily (<c>SteadyUploadAttribute</c>, or <c>SteadyIntegrationBodies</c> for the Integration API's, which its call audit reads).
    /// </summary>
    public static IReadOnlySet<string> ConcurrencyPolicies { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        UploadPolicy, BacktestPolicy, ValidationResultsPolicy, ReportsPolicy, IntegrationBatchPolicy, IntegrationAidxPolicy
    };

    /// <summary>The Retry-After of a 429 from a concurrency policy, whose lease names no time (uploads, batches, backtests, validation results, reports).</summary>
    public const int ConcurrencyRetryAfterSeconds = 10;

    /// <summary>
    /// ARV-096b: the partition of a request to a user's concurrency policy (<see cref="UploadPolicy"/>, <see cref="BacktestPolicy"/>,
    /// <see cref="ValidationResultsPolicy"/>, <see cref="ReportsPolicy"/>) that sends no credentials at all: no limit. The policies run
    /// before authentication, and such a request ends 401 there at once, so it must neither take a slot nor wait for one.
    /// </summary>
    public const string WithoutCredentialsPartition = "without-credentials";

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
            .Validate(settings => settings.Global.IsValid && settings.Auth.IsValid && settings.Device.IsValid && settings.DisplayPlays.IsValid && settings.IntegrationAuth.IsValid &&
                                settings.IntegrationBatch.IsValid && settings.IntegrationAidx.IsValid && settings.IntegrationClient.IsValid &&
                                settings.ValidationResults.IsValid && settings.Reports.IsValid && settings.CsvImports.IsValid && settings.Uploads.IsValid,
                $"{RateLimitingSettings.SectionName} limits need a positive PermitLimit and WindowSeconds, a QueueLimit of zero or more and, for a sliding window, at most 86,400 WindowSeconds in 1 to 600 SegmentsPerWindow (no more than WindowSeconds).")
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
                // ARV-091: each named policy logs its own rejections (the policy and the kind of partition, never the key);
                // OnRejected below is the global limiter's, which the framework calls for its rejections and for none of these.
                options.AddPolicy(AuthPolicy, Logged(AuthPolicy, AddressKind, context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => ToLimiterOptions(limits.Auth))));
                options.AddPolicy(IntegrationAuthPolicy, Logged(IntegrationAuthPolicy, AddressKind, context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => ToLimiterOptions(limits.IntegrationAuth))));
                options.AddPolicy(DevicePolicy, Logged(DevicePolicy, DeviceKind, context =>
                    RateLimitPartition.GetFixedWindowLimiter(Ariva.Api.Common.Security.DeviceAuthentication.RateLimitPartition(context), _ => ToLimiterOptions(limits.Device))));
                options.AddPolicy(DisplayPolicy, Logged(DisplayPolicy, DisplayKind, context =>
                    RateLimitPartition.GetFixedWindowLimiter(Ariva.Api.Common.Security.DisplayAuthentication.RateLimitPartition(context), _ => ToLimiterOptions(limits.Device))));
                options.AddPolicy(DisplayPlaysPolicy, Logged(DisplayPlaysPolicy, DisplayKind, context =>
                    RateLimitPartition.GetFixedWindowLimiter(Ariva.Api.Common.Security.DisplayAuthentication.RateLimitPartition(context),
                        _ => ToLimiterOptions(limits.DisplayPlays))));
                options.AddPolicy(UploadPolicy, Logged(UploadPolicy, HostKind, context => SessionConcurrency(context, UploadPolicy, 1, 2)));
                options.AddPolicy(IntegrationBatchPolicy, Logged(IntegrationBatchPolicy, HostKind, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(IntegrationBatchPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = limits.IntegrationBatch.PermitLimit,
                        QueueLimit = limits.IntegrationBatch.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    })));
                options.AddPolicy(IntegrationAidxPolicy, Logged(IntegrationAidxPolicy, HostKind, _ =>
                    RateLimitPartition.GetConcurrencyLimiter(IntegrationAidxPolicy, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = limits.IntegrationAidx.PermitLimit,
                        QueueLimit = limits.IntegrationAidx.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    })));
                options.AddPolicy(ValidationResultsPolicy, Logged(ValidationResultsPolicy, HostKind, context => SessionConcurrency(context, ValidationResultsPolicy, limits.ValidationResults.PermitLimit, limits.ValidationResults.QueueLimit)));
                options.AddPolicy(ReportsPolicy, Logged(ReportsPolicy, HostKind, context => SessionConcurrency(context, ReportsPolicy, limits.Reports.PermitLimit, limits.Reports.QueueLimit)));
                options.AddPolicy(BacktestPolicy, Logged(BacktestPolicy, HostKind, context => SessionConcurrency(context, BacktestPolicy, 2, 2)));
                options.OnRejected = (context, _) => Rejected(context, SecurityEvents.GlobalPolicy, AddressKind);
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

    /// <summary>
    /// A 429 answer (unchanged since ARV-104g: Retry-After from a window limiter's lease, else a fixed delay) and its security
    /// event (ARV-091), with the policy and the kind of partition only.
    /// </summary>
    private static ValueTask Rejected(OnRejectedContext context, string policy, Func<HttpContext, string> partitionKind)
    {
        // A window limiter's lease says when the window reopens; a concurrency limiter's lease carries no such
        // metadata (a permit frees when a request ends), so its 429 says to retry after a fixed delay instead
        // (first security review of ARV-104g, M1: the validation-results 429 had no Retry-After).
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : ConcurrencyRetryAfterSeconds;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        context.HttpContext.RequestServices?.GetService<SecurityEventLog>()?.RateLimited(context.HttpContext, policy, partitionKind);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// ARV-096b: whether a request sends any credentials, read as JWT authentication reads them: the Authorization header's values
    /// joined (<c>Headers.Authorization.ToString()</c>, which drops empty ones). Only a request whose joined value is empty or white
    /// space goes uncounted, and authentication refuses it 401 at once; any other (a user's token, a forged one, a device key, Basic)
    /// is counted, for the moment its authentication takes at least. A bearer token is the only way an API call proves a user's session
    /// (the refresh cookie goes to <c>/api/auth</c> only; the live hub's query token is not on these endpoints). Security review of
    /// ARV-096b, H1: counting exactly one value let a signed-in caller add an empty second one and go uncounted.
    /// </summary>
    public static bool PresentsCredentials(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !string.IsNullOrWhiteSpace(context.Request.Headers.Authorization.ToString());
    }

    /// <summary>
    /// ARV-096b: a user's concurrency policy, one partition per host process for the requests that send credentials
    /// (<see cref="PresentsCredentials"/>), and none for the others (<see cref="WithoutCredentialsPartition"/>).
    /// </summary>
    public static RateLimitPartition<string> SessionConcurrency(HttpContext context, string policy, int permits, int queue) =>
        PresentsCredentials(context)
            ? RateLimitPartition.GetConcurrencyLimiter(policy, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = permits,
                QueueLimit = queue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            })
            : RateLimitPartition.GetNoLimiter(WithoutCredentialsPartition);

    /// <summary>A named policy whose rejections are <see cref="Rejected"/>.</summary>
    private static LoggedPolicy Logged(string policy, Func<HttpContext, string> partitionKind, Func<HttpContext, RateLimitPartition<string>> partitioner) =>
        new(partitioner, (context, _) => Rejected(context, policy, partitionKind));

    private static string AddressKind(HttpContext context) => SecurityEvents.AddressPartition;

    private static string HostKind(HttpContext context) => SecurityEvents.HostPartition;

    /// <summary>The device policy's partition: the presented credential's hash when it is well formed, else the address; only the kind is kept.</summary>
    private static string DeviceKind(HttpContext context) =>
        Ariva.Api.Common.Security.DeviceAuthentication.RateLimitPartition(context).StartsWith("address:", StringComparison.Ordinal)
            ? SecurityEvents.AddressPartition
            : SecurityEvents.DeviceCredentialPartition;

    /// <summary>The display policies' partition kind, as <see cref="DeviceKind"/>.</summary>
    private static string DisplayKind(HttpContext context) =>
        Ariva.Api.Common.Security.DisplayAuthentication.RateLimitPartition(context).StartsWith("address:", StringComparison.Ordinal)
            ? SecurityEvents.AddressPartition
            : SecurityEvents.DisplayCredentialPartition;

    /// <summary>A named policy with its own rejection callback; it partitions exactly as the partitioner it is given.</summary>
    private sealed class LoggedPolicy(Func<HttpContext, RateLimitPartition<string>> partitioner, Func<OnRejectedContext, CancellationToken, ValueTask> onRejected)
        : IRateLimiterPolicy<string>
    {
        public Func<OnRejectedContext, CancellationToken, ValueTask> OnRejected { get; } = onRejected;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext) => partitioner(httpContext);
    }

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
