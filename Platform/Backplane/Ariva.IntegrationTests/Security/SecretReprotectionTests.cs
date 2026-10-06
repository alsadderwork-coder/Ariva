using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Ariva.Di.Extensions;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-080 (ASVS V11.2.2): after the Data Protection key rolls, the re-protection round rewrites stored TOTP seeds under
/// the new key, leaves current ones alone and never touches a value it cannot read.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SecretReprotectionTests(PostgresFixture fixture)
{
    private const string Seed = "JBSWY3DPEHPK3PXP";

    [Fact]
    public async Task Round_Should_ReprotectASeedUnderTheNewKey_When_TheKeyHasRolled()
    {
        await using var host = Host();
        var userId = await host.CreateUserAsync("reprotect.rolled");
        var connection = fixture.ConnectionString(await host.DatabaseAsync());
        var protector = (IPersistedDataProtector)host.Provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose);
        var underOldKey = protector.Protect(Seed);
        await SetSeedAsync(connection, userId, underOldKey);

        var keys = host.Provider.GetRequiredService<IKeyManager>();
        // The default key is the most recently activated one: activate the new key after the first (within the clock skew).
        keys.CreateNewKey(DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow.AddDays(90));
        // The key ring refreshes in the background: wait until the new key is the default one.
        for (var attempt = 0; attempt < 100 && !Unprotected(protector, underOldKey).RequiresMigration; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        var round = host.Provider.GetRequiredService<SecretReprotectionRound>();

        var first = await round.RunAsync(TestContext.Current.CancellationToken);
        var rewritten = await SeedAsync(connection, userId);
        var second = await round.RunAsync(TestContext.Current.CancellationToken);

        first.Reprotected.Should().Be(1);
        rewritten.Should().NotBe(underOldKey);
        protector.Unprotect(rewritten).Should().Be(Seed, "the secret is the same, only its key changed");
        Unprotected(protector, rewritten).RequiresMigration.Should().BeFalse("it is now under the default key");
        (await SeedAsync(connection, userId)).Should().Be(rewritten, "a value under the current key is not rewritten");
        second.Reprotected.Should().Be(0);
    }

    [Fact]
    public async Task Round_Should_LeaveAValueAlone_When_ItCannotBeRead()
    {
        await using var host = Host();
        var userId = await host.CreateUserAsync("reprotect.unreadable");
        var connection = fixture.ConnectionString(await host.DatabaseAsync());
        const string Unreadable = "CfDJ8not-a-data-protection-payload";
        await SetSeedAsync(connection, userId, Unreadable);

        var run = await host.Provider.GetRequiredService<SecretReprotectionRound>().RunAsync(TestContext.Current.CancellationToken);

        run.Failed.Should().BeGreaterThanOrEqualTo(1);
        (await SeedAsync(connection, userId)).Should().Be(Unreadable);
    }

    private AccountsHost Host() =>
        new(fixture, database: TestDatabase.SecretReprotection, configure: services =>
        {
            // The hosts' Data Protection (OAEP and GCM key ring) with an in-memory repository, instead of the ephemeral one.
            services.RemoveAll<IDataProtectionProvider>();
            services.AddArivaDataProtection(new DatabaseSettings(), DataProtectionCertificates.From(Certificate()));
            var repository = new MemoryRepository();
            services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        });

    private static (string Plaintext, bool RequiresMigration) Unprotected(IPersistedDataProtector protector, string value)
    {
        var bytes = protector.DangerousUnprotect(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(value), false, out var requiresMigration, out _);
        return (System.Text.Encoding.UTF8.GetString(bytes), requiresMigration);
    }

    private static async Task SetSeedAsync(string connectionString, Guid userId, string value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""UPDATE "user" SET totp_secret_protected = @value WHERE id = @id""", connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("id", userId);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    private static async Task<string> SeedAsync(string connectionString, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""SELECT totp_secret_protected FROM "user" WHERE id = @id""", connection);
        command.Parameters.AddWithValue("id", userId);
        return (string)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        var request = new CertificateRequest("CN=Ariva DP reprotect", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private sealed class MemoryRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
                return [.. _elements.Select(e => new XElement(e))];
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
                _elements.Add(new XElement(element));
        }
    }
}
