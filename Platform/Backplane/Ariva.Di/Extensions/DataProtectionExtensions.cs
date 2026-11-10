using Ariva.Infra.DataProtection;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ariva.Di.Extensions;

/// <summary>
/// ASP.NET Core Data Protection with one key ring for every host (ARV-008): keys in PostgreSQL through
/// <see cref="PostgresXmlRepository"/>, encrypted at rest with the certificate from the ariva-dataprotection secret
/// (<see cref="DataProtectionCertificates"/>) by <see cref="OaepGcmXmlEncryptor"/> (RSA-OAEP-SHA256 and AES-256-GCM,
/// ARV-080), and signed since ARV-211 with the key ring's own signing certificate from the ariva-dataprotection-signing
/// secret (<see cref="KeyRingSigning"/>, ES256), each element and each row (script 0073). Keys roll every 90 days. The
/// application name is fixed, so every host and every release reads the same purpose strings.
/// </summary>
public static class DataProtectionExtensions
{
    public const string ApplicationName = "ariva";

    public static IServiceCollection AddArivaDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var certificates = DataProtectionCertificates.Load(configuration);
        var database = DatabaseSettings.FromConfiguration(configuration);
        services.AddArivaDataProtection(database, certificates, KeyRingSigning.Load(configuration, certificates), LegacyFormatUntil(configuration));
        // ARV-211 (the review's finding H1): outside vm-local, after the schema check, a host refuses to start while a stored value
        // needs a key from before ARV-211 and the upgrade window is not open, and warns while it is open.
        if (database.VerifySchemaOnStartup)
            services.AddHostedService<KeyRingUpgradeGate>();
        return services;
    }

    /// <summary>
    /// DataProtection:LegacyFormatUntil (ARV-080): until when keys written in the framework's older format are still read,
    /// so stored secrets can be re-protected; unset, they are refused. An ISO 8601 date or time, UTC when no offset is given.
    /// </summary>
    public static DateTimeOffset? LegacyFormatUntil(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration["DataProtection:LegacyFormatUntil"];
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var until)
            ? until
            : throw new InvalidOperationException($"DataProtection:LegacyFormatUntil '{value}' is not a date (use YYYY-MM-DD).");
    }

    public static IServiceCollection AddArivaDataProtection(this IServiceCollection services, DatabaseSettings database, DataProtectionCertificates certificates,
        KeyRingSigning signing, DateTimeOffset? legacyFormatUntil = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(signing);

        services.AddSingleton(certificates);
        services.AddSingleton(signing);
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
            // certificate only while DataProtection:LegacyFormatUntil admits them (TrustedKeyRingRepository); nothing new is
            // written that way.
            .UnprotectKeysWithAnyCertificate([.. certificates.All]);

        // ARV-080: our repository behind the trusted key filter, and new keys wrapped with RSA-OAEP-SHA256 and AES-256-GCM
        // and signed (OaepGcmXmlEncryptor); ARV-211: signed with the key ring's own signing key, checked before a key enters.
        services.AddOptions<KeyManagementOptions>().Configure<ILoggerFactory>((options, loggers) =>
        {
            options.XmlRepository = new TrustedKeyRingRepository(new PostgresXmlRepository(database), certificates, signing, legacyFormatUntil, TimeProvider.System,
                loggers.CreateLogger<TrustedKeyRingRepository>());
            options.XmlEncryptor = new OaepGcmXmlEncryptor(certificates, signing);
        });
        return services;
    }
}
