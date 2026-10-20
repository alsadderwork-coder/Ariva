using Ariva.Api.Common.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// AMAN's AddAppLogging: Serilog replaces the default providers for the host (ARV-007). The host owns its logger
/// (<c>preserveStaticLogger</c>, ARV-091): Serilog's static <c>Log.Logger</c> is neither set nor closed, so a host's
/// shutdown never closes another logger in the same process (test hosts run one after another in one process, and a host's
/// entry point disposes it after the test has moved on). No Ariva code writes to the static logger.
/// </summary>
public static class LoggingExtensions
{
    public static WebApplicationBuilder AddAppLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        ArivaLogging.AddCollectorClients(builder.Services, builder.Configuration);
        builder.Services.AddSerilog((services, logger) =>
            ArivaLogging.Configure(logger, builder.Configuration, builder.Environment.EnvironmentName, services.GetService<SecurityEventMetrics>(), services),
            preserveStaticLogger: true);
        return builder;
    }
}
