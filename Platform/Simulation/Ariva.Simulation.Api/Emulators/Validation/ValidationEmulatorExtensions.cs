using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>
/// Registers the validation observer emulator (ARV-104i): its settings validated at start (observer accounts, bounds), and the
/// Ariva.Api.Main client: the address from configuration only (<c>Simulation:Ariva:MainUrl</c>, never from a request, CWE-918),
/// no redirects, no cookies (Ariva's refresh cookie is never kept) and no environment proxy (a proxy would see the password and
/// the tokens of a plain-HTTP in-cluster call), a bounded answer and no request logging.
/// </summary>
public static class ValidationEmulatorExtensions
{
    public static IServiceCollection AddValidationEmulator(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<ValidationEmulatorSettings>()
            .Bind(configuration.GetSection(ValidationEmulatorSettings.Section))
            .ValidateDataAnnotations() // runs ValidationEmulatorSettings.Validate (IValidatableObject)
            .ValidateOnStart();

        services
            .AddHttpClient(ValidationEmulator.HttpClientName, (provider, client) =>
            {
                var target = provider.GetRequiredService<IOptionsMonitor<ArivaTargetSettings>>().CurrentValue;
                if (!string.IsNullOrEmpty(target.MainUrl))
                    client.BaseAddress = new Uri(target.MainUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(target.RequestTimeoutSeconds);
                // A capture list of a site holds a few campaigns with their lines, zones and desks.
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            .RemoveAllLoggers();

        services.AddSingleton<ValidationEmulator>();
        return services;
    }
}
