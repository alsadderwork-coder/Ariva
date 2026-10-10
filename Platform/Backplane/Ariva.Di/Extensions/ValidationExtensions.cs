using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Ariva.Infra.Services.Validation;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// The validation results (ARV-104g2 service, served and frozen by ARV-104g): Ariva.Api.Main only, the host of the validation reader
/// login (ARV-104g1). Registers the settings (<c>Validation:Results</c>, refused at start when out of bounds), the host-wide
/// single-flight of computations (a singleton: one computation per campaign and purpose, at most
/// <see cref="ValidationResultsSettings.MaxConcurrentComputations"/> at once), the service (scoped; its interface exposes only the
/// site-scoped read and the recomputation), the background freeze of closed campaigns and the request timeout policy of the results
/// endpoints. Call after <c>AddArivaTokenIssuing</c> (the service reads the caller's stored roles and writes audit entries).
/// </summary>
public static class ValidationExtensions
{
    public static IServiceCollection AddArivaValidationResults(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = ValidationResultsSettings.FromConfiguration(configuration);
        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new SingleFlight<ValidationResultsViewModel>(settings.MaxConcurrentComputations, settings.Timeout,
            provider.GetRequiredService<TimeProvider>(), ValidationResultsErrors.TimedOut));
        services.TryAddScoped<SvcValidationResults>();
        services.TryAddScoped<ISvcValidationResults>(provider => provider.GetRequiredService<SvcValidationResults>());
        services.AddHostedService<ValidationResultsFreezer>();

        // A request for the results waits at most RequestTimeout (the computation it waited for goes on): 503 with Retry-After.
        services.AddRequestTimeouts(options => options.AddPolicy(ValidationResultsSettings.RequestTimeoutPolicy, new RequestTimeoutPolicy
        {
            Timeout = settings.RequestTimeout,
            TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable,
            WriteTimeoutResponse = context =>
            {
                context.Response.Headers.RetryAfter = "30";
                return Task.CompletedTask;
            }
        }));
        return services;
    }
}
