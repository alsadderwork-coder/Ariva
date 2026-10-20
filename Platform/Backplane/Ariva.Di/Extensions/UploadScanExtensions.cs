using Ariva.Core.Services.Storage;
using Ariva.Infra.Services.Displays;
using Ariva.Infra.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ariva.Di.Extensions;

/// <summary>
/// The malware scan of uploads (ARV-092b), Ariva.Api.Main only: registered with the upload services
/// (<see cref="AccountExtensions.AddArivaTokenIssuing"/>, <see cref="FlightExtensions.AddArivaFlightSchedules"/>), once
/// whichever comes first. The settings (<c>MalwareScan</c>) are checked when the host starts, their stream limit against
/// the largest upload this installation takes; k8s-prd does not start without a scanner.
/// </summary>
public static class UploadScanExtensions
{
    public static IServiceCollection AddArivaUploadScanning(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(d => d.ServiceType == typeof(IMalwareScanner)))
            return services;

        var environment = configuration["Application:Environment"];
        var settings = configuration.GetSection(MalwareScanSettings.SectionName).Get<MalwareScanSettings>() ?? new MalwareScanSettings();
        // As Redis and PostgreSQL (ARV-082a): production refuses a configuration it cannot run safely, before anything starts.
        if (environment == "k8s-prd" && settings.ProductionProblem() is { } problem)
            throw new InvalidOperationException(problem);

        var videos = configuration.GetSection(AdvertisingVideoSettings.SectionName).Get<AdvertisingVideoSettings>() ?? new AdvertisingVideoSettings();
        services.AddOptions<MalwareScanSettings>()
            .Bind(configuration.GetSection(MalwareScanSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MalwareScanSettings>>(new MalwareScanSettingsValidation(MalwareScanSettings.LargestUpload(videos.MaxBytes)));
        services.TryAddSingleton(UploadScanPolicy.For(environment));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMalwareScanner, ClamdScanner>();
        services.TryAddScoped<UploadScreening>();
        if (!settings.Configured)
            services.AddHostedService<UploadScanStartNotice>();
        return services;
    }
}
