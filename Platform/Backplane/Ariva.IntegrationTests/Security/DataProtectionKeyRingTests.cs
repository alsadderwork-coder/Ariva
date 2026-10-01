using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Di.Extensions;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-008: two hosts share the Data Protection key ring through PostgreSQL, and the stored keys cannot be used
/// without the key-protection certificate.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DataProtectionKeyRingTests(PostgresFixture fixture)
{
    private const string Purpose = "Ariva.IntegrationTests.KeyRing";

    [Fact]
    public async Task Unprotect_Should_ReadOtherHostsPayload_When_HostsShareKeyRingAndCertificate()
    {
        var database = await MigratedDatabaseAsync();
        using var certificate = CreateCertificate("CN=Ariva DP test A");
        var settings = Settings(database);

        await using var hostA = Host(settings, DataProtectionCertificates.From(certificate));
        await using var hostB = Host(settings, DataProtectionCertificates.From(certificate));
        await using var hostWithOtherCertificate = Host(settings, DataProtectionCertificates.From(CreateCertificate("CN=Ariva DP test other")));

        var protectedByA = Protector(hostA).Protect("refresh-token-family-42");

        Protector(hostB).Unprotect(protectedByA).Should().Be("refresh-token-family-42");

        var withoutCertificate = () => Protector(hostWithOtherCertificate).Unprotect(protectedByA);
        withoutCertificate.Should().Throw<CryptographicException>("the key in the database is encrypted with a certificate this host does not hold");

        var stored = await StoredKeysAsync(database);
        stored.Should().NotBeEmpty();
        stored.Should().AllSatisfy(xml =>
        {
            xml.Should().Contain("EncryptedData", "keys are encrypted with the certificate before they are stored");
            xml.Should().NotContain("<value>", "no plaintext master key may reach the table");
        });
    }

    private async Task<string> MigratedDatabaseAsync()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.DataProtection);
        await new SqlScriptRunner(fixture.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        return database;
    }

    private DatabaseSettings Settings(string database) => new()
    {
        Host = fixture.Hostname,
        Port = fixture.Port,
        Name = database,
        Username = fixture.AdminUsername,
        Password = fixture.AdminPassword
    };

    private static ServiceProvider Host(DatabaseSettings settings, DataProtectionCertificates certificates)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaDataProtection(settings, certificates);
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(IServiceProvider host) =>
        host.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Round trip through PKCS#12 so the private key is usable on every platform, as a mounted PEM pair would be.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private async Task<List<string>> StoredKeysAsync(string database)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT xml FROM data_protection_key", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
