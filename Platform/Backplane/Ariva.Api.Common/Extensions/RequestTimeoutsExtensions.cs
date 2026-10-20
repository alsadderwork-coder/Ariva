using System.Globalization;
using Ariva.Api.Common.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Request timeouts for Ariva.Api.Main (ARV-096, CWE-400, ASVS 5.0 V15.2.2), read from <c>Security:RequestTimeouts</c>: a default
/// policy for every endpoint without its own, and the named policies below. A timeout cancels <c>HttpContext.RequestAborted</c>
/// (the action's <see cref="CancellationToken"/>); a request whose answer has not started then gets 503 with Retry-After. Run
/// <c>UseRequestTimeouts</c> after routing; exempt an endpoint that is long by design with <c>DisableRequestTimeout</c> (the live hub).
/// Only Ariva.Api.Main registers these (the other hosts: <see cref="RequestTimeoutsSettings"/>).
/// </summary>
public static class RequestTimeoutsExtensions
{
    #region Constants

    /// <summary>
    /// The on-demand daily report and its CSV files (<c>[RequestTimeout(ReportsPolicy)]</c>), beside the concurrency policy of the
    /// same name (<see cref="RateLimitingExtensions.ReportsPolicy"/>).
    /// </summary>
    public const string ReportsPolicy = "reports";

    /// <summary>
    /// Endpoints that move a large file: every endpoint under the <c>upload</c> concurrency policy, and the stored floor plans and
    /// advertisements served whole (<c>[RequestTimeout(TransferPolicy)]</c>).
    /// </summary>
    public const string TransferPolicy = "transfer";

    #endregion

    #region Services

    /// <summary>
    /// Binds and validates <see cref="RequestTimeoutsSettings"/> (refused at start when out of bounds) and registers the default
    /// policy and the <see cref="ReportsPolicy"/> and <see cref="TransferPolicy"/> policies, each answering 503 with Retry-After.
    /// The options are bound lazily, so configuration added by tests or by later configuration sources is honoured.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppRequestTimeouts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RequestTimeoutsSettings>()
            .Bind(configuration.GetSection(RequestTimeoutsSettings.SectionName))
            .Validate(settings => settings.IsValid, $"{RequestTimeoutsSettings.SectionName}: DefaultSeconds and ReportsSeconds are 5 to 600, TransferSeconds 60 to 3,600.")
            .ValidateOnStart();

        services.AddRequestTimeouts();
        services
            .AddOptions<RequestTimeoutOptions>()
            .Configure<IOptions<RequestTimeoutsSettings>>((options, settings) =>
            {
                var timeouts = settings.Value;
                options.DefaultPolicy = Policy(timeouts.DefaultSeconds);
                options.AddPolicy(ReportsPolicy, Policy(timeouts.ReportsSeconds));
                options.AddPolicy(TransferPolicy, Policy(timeouts.TransferSeconds));
            });

        return services;
    }

    #endregion

    #region Helpers

    /// <summary>A policy of the given length whose timeout answers 503 with Retry-After (the status code pages write the body).</summary>
    private static RequestTimeoutPolicy Policy(int seconds) => new()
    {
        Timeout = TimeSpan.FromSeconds(seconds),
        TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable,
        WriteTimeoutResponse = context =>
        {
            context.Response.Headers.RetryAfter = RequestTimeoutsSettings.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }
    };

    #endregion
}
