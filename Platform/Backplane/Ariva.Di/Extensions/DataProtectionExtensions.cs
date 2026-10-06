using Ariva.Infra.DataProtection;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// ASP.NET Core Data Protection with one key ring for every host (ARV-008): keys in PostgreSQL through
/// <see cref="PostgresXmlRepository"/>, encrypted at rest with the certificate from the ariva-dataprotection secret
/// (<see cref="DataProtectionCertificates"/>) by <see cref="OaepGcmXmlEncryptor"/> (RSA-OAEP-SHA256 and AES-256-GCM,
/// ARV-080). Keys roll every 90 days. The application name is fixed, so every host
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

        services.AddSingleton(certificates);
        // ARV-080: re-protects stored secrets under the current key; Cronz runs it (SecretJobs).
        services.TryAddScoped<SecretReprotection>();
        services.TryAddSingleton<SecretReprotectionRound>();
        services
            .AddDataProtection()
            .SetApplicationName(ApplicationName)
            .SetDefaultKeyLifetime(TimeSpan.FromDays(90))
            // ARV-080: new keys protect payloads with AES-256-GCM; existing keys keep their own algorithm until they roll.
            .UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
            {
                EncryptionAlgorithm = EncryptionAlgorithm.AES_256_GCM,
                ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
            })
            // Keys written before ARV-080 (EncryptedXml: RSA PKCS#1 v1.5 and AES-256-CBC) stay readable with any configured
            // certificate until they expire; nothing new is written that way.
            .UnprotectKeysWithAnyCertificate([.. certificates.All]);

        // ARV-080: our repository, and new keys wrapped with RSA-OAEP-SHA256 and AES-256-GCM (OaepGcmXmlEncryptor).
        services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = new PostgresXmlRepository(database);
            options.XmlEncryptor = new OaepGcmXmlEncryptor(certificates);
        });
        return services;
    }
}
