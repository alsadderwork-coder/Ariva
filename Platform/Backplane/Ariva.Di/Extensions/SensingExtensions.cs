using Ariva.Core.Services.Sensing;
using Ariva.Infra.Sensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>The sensing ingest of Ariva.Api.Ingest (ARV-023): mappers, clock estimates and the Kafka sink.</summary>
public static class SensingExtensions
{
    public static IServiceCollection AddArivaSensingIngest(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<IngestSettings>()
            .Bind(configuration.GetSection(IngestSettings.SectionName))
            .Validate(s => s.MaxEventsPerMessage is > 0 and <= IngestSettings.MaxEventsLimit, $"Ingest:MaxEventsPerMessage is from 1 to {IngestSettings.MaxEventsLimit}.")
            .ValidateOnStart();
        services.TryAddSingleton<DeviceClockStore>();
        services.TryAddSingleton<ISensingSink, MessageBusSensingSink>();
        services.TryAddScoped<SensingIngest>();
        return services;
    }
}
