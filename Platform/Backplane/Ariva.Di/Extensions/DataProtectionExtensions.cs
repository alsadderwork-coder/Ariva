using Ariva.Infra.DataProtection;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Di.Extensions;

/// <summary>
/// ASP.NET Core Data Protection with one key ring for every host (ARV-008): keys in PostgreSQL through
/// <see cref="PostgresXmlRepository"/>, encrypted at rest with the certificate from the ariva-dataprotection secret
/// (<see cref="DataProtectionCertificates"/>). Keys roll every 90 days. The application name is fixed, so every host
/// and every release reads the same purpose strings.
/// </summary>
public static class DataProtectionExtensions
{
    public const string ApplicationName = "ariva";

    public static IServiceCollection AddArivaDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddArivaDataProtection(DatabaseSettings.FromConfiguration(configuration), DataProtectionCertificates.Load(configuration));
    }

    public static IServiceCollection AddArivaDataProtection(this IServiceCollection services, DatabaseSettings database, DataProtectionCertificates certificates)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(certificates);

        services
            .AddDataProtection()
            .SetApplicationName(ApplicationName)
            .SetDefaultKeyLifetime(TimeSpan.FromDays(90))
            .ProtectKeysWithCertificate(certificates.Current)
            .UnprotectKeysWithAnyCertificate([.. certificates.All]);

        // After ProtectKeysWithCertificate: the repository is ours, the encryptor stays the certificate one.
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = new PostgresXmlRepository(database));
        return services;
    }
}
