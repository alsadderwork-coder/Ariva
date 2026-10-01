using Ariva.Api.Common.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Ariva.Api.Common.Extensions;

/// <summary>AMAN's AddAppLogging: Serilog replaces the default providers for the host (ARV-007).</summary>
public static class LoggingExtensions
{
    public static WebApplicationBuilder AddAppLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((_, logger) =>
            ArivaLogging.Configure(logger, builder.Configuration, builder.Environment.EnvironmentName));
        return builder;
    }
}
