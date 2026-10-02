using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Emulators.Sensors;

/// <summary>Registers the sensor emulator (ARV-028): settings validated at start, the Ingest client and the background player.</summary>
public static class SensorEmulatorExtensions
{
    public static IServiceCollection AddSensorEmulator(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<SensorEmulatorSettings>()
            .Bind(configuration.GetSection(SensorEmulatorSettings.Section))
            .ValidateDataAnnotations() // runs SensorEmulatorSettings.Validate (IValidatableObject)
            .ValidateOnStart();

        services
            .AddHttpClient(SensorEmulator.HttpClientName, (provider, client) =>
            {
                var settings = provider.GetRequiredService<IOptionsMonitor<SensorEmulatorSettings>>().CurrentValue;
                if (!string.IsNullOrEmpty(settings.IngestUrl))
                    client.BaseAddress = new Uri(settings.IngestUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
                client.MaxResponseContentBufferSize = 64 * 1024; // Ingest answers with a small outcome
            })
            // The credential goes to IngestUrl and nowhere else: no redirects, no cookies and no environment proxy
            // (a proxy would see the Authorization header of a plain-HTTP in-cluster push).
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            // Four lines per push at up to two pushes a second per device is noise; failures are logged by the emulator.
            .RemoveAllLoggers();

        services.AddSingleton<SensorEmulator>();
        services.AddHostedService(provider => provider.GetRequiredService<SensorEmulator>());
        return services;
    }
}
