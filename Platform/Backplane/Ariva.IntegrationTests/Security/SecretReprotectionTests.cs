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
using Microsoft.Extensions.Logging;
using Ariva.Core.Services;
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

        // The default key is the most recently activated one: activate the new key after the first.
        await RollKeyAsync(host, protector, underOldKey);
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
        _logs.Entries.Should().Contain(e => e.Contains(userId.ToString(), StringComparison.Ordinal), "the row that could not be read is named");
        _logs.Entries.Should().NotContain(e => e.Contains(Unreadable, StringComparison.Ordinal), "a stored secret never reaches a log (CWE-532)");
    }

    private readonly CapturedLogs _logs = new();

    private AccountsHost Host() =>
        new(fixture, database: TestDatabase.SecretReprotection, configure: services =>
        {
            // The hosts' Data Protection (signed OAEP and GCM key ring) with an in-memory repository, instead of the
            // ephemeral one.
            services.RemoveAll<IDataProtectionProvider>();
            services.AddArivaDataProtection(new DatabaseSettings(), DataProtectionCertificates.From(Certificate()));
            var repository = new MemoryRepository();
            services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
            services.AddSingleton<ILoggerProvider>(_logs);
        });

    /// <summary>Makes a new key the default and waits until the background key ring refresh has picked it up.</summary>
    private static async Task RollKeyAsync(AccountsHost host, IPersistedDataProtector protector, string underOldKey)
    {
        host.Provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow.AddDays(90));
        for (var attempt = 0; attempt < 100 && !Unprotected(protector, underOldKey).RequiresMigration; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Round_Should_ReprotectClientSeedsAndOutboundSecrets_When_TheKeyHasRolled()
    {
        const string Secret = "{\"ApiKey\":\"partner-key\"}";
        await using var host = Host();
        await host.CreateUserAsync("reprotect.tables");
        var connection = fixture.ConnectionString(await host.DatabaseAsync());
        var provider = host.Provider.GetRequiredService<IDataProtectionProvider>();
        var seeds = (IPersistedDataProtector)provider.CreateProtector(Ariva.Infra.Integration.IntegrationCredentials.SeedProtectionPurpose);
        var outbound = (IPersistedDataProtector)provider.CreateProtector(Ariva.Infra.Integration.OutboundSecrets.Purpose);
        var seed = seeds.Protect(Seed);
        var secret = outbound.Protect(Secret);
        var clientId = Guid.CreateVersion7();
        var endpointId = Guid.CreateVersion7();
        await ExecuteAsync(connection,
            """
            INSERT INTO integration_client (id, client_id, name, kind, status, scope_names, site_codes, allowed_networks, require_totp_per_request,
                secret_algorithm, secret_iterations, secret_salt, secret_hash, secret_changed_utc, totp_secret_protected, totp_changed_utc,
                tokens_valid_from_utc, token_version)
            VALUES (@id, 'ic_abcdefghijklmnopqrstuvwxyz', 'Reprotect client', 'Other', 'Active', 'queues:read', 'AMM', '10.0.0.0/8', false,
                'pbkdf2-sha256', 600000, 'salt', 'hash', now(), @value, now(), now(), 0)
            """, clientId, seed);
        await ExecuteAsync(connection,
            """
            INSERT INTO outbound_endpoint (id, code, name, purpose, status, site_codes, base_url, allowed_networks, auth_kind, header_name,
                totp_per_request, timeout_seconds, retry_count, breaker_failures, break_seconds, poll_seconds, secret_protected,
                has_client_certificate, secret_changed_utc, client_version)
            VALUES (@id, 'reprotect-partner', 'Reprotect partner', 'Generic', 'Active', 'AMM', 'https://partner.example/', '10.0.0.0/8', 'ApiKeyHeader', 'X-Api-Key',
                false, 10, 0, 5, 30, 0, @value, false, now(), 0)
            """, endpointId, secret);

        await RollKeyAsync(host, seeds, seed);
        var run = await host.Provider.GetRequiredService<SecretReprotectionRound>().RunAsync(TestContext.Current.CancellationToken);

        var newSeed = await ScalarAsync(connection, "SELECT totp_secret_protected FROM integration_client WHERE id = @id", clientId);
        var newSecret = await ScalarAsync(connection, "SELECT secret_protected FROM outbound_endpoint WHERE id = @id", endpointId);
        run.Reprotected.Should().BeGreaterThanOrEqualTo(2);
        newSeed.Should().NotBe(seed);
        seeds.Unprotect(newSeed).Should().Be(Seed);
        Unprotected(seeds, newSeed).RequiresMigration.Should().BeFalse();
        newSecret.Should().NotBe(secret);
        outbound.Unprotect(newSecret).Should().Be(Secret);
    }

    [Fact]
    public async Task Round_Should_KeepTheNewValue_When_ASecretChangesWhileItIsReprotected()
    {
        await using var host = Host();
        var userId = await host.CreateUserAsync("reprotect.race");
        var connection = fixture.ConnectionString(await host.DatabaseAsync());
        var protector = (IPersistedDataProtector)host.Provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose);
        var underOldKey = protector.Protect(Seed);
        await SetSeedAsync(connection, userId, underOldKey);
        await RollKeyAsync(host, protector, underOldKey);
        var reEnrolled = protector.Protect("NEWSEEDNEWSEED22");

        await using var scope = host.Provider.CreateAsyncScope();
        var reprotection = scope.ServiceProvider.GetRequiredService<SecretReprotection>();
        // The user re-enrols between the round's read and its write (committed on another connection).
        reprotection.BeforeWrite = async id =>
        {
            if (id == userId)
                await SetSeedAsync(connection, userId, reEnrolled);
        };
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await reprotection.RunAsync(TestContext.Current.CancellationToken);
        await unitOfWork.EndAsync(TestContext.Current.CancellationToken);

        (await SeedAsync(connection, userId)).Should().Be(reEnrolled, "the compare-and-swap UPDATE never overwrites a value that changed");
    }

    [Fact]
    public async Task Round_Should_LeaveAValueAlone_When_ItsKeyIsRevoked()
    {
        await using var host = Host();
        var userId = await host.CreateUserAsync("reprotect.revoked");
        var connection = fixture.ConnectionString(await host.DatabaseAsync());
        var protector = (IPersistedDataProtector)host.Provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose);
        var underOldKey = protector.Protect(Seed);
        await SetSeedAsync(connection, userId, underOldKey);
        var keys = host.Provider.GetRequiredService<IKeyManager>();
        var oldKey = keys.GetAllKeys().Single();
        await RollKeyAsync(host, protector, underOldKey);
        keys.RevokeKey(oldKey.KeyId, "ARV-080 test: compromised");
        for (var attempt = 0; attempt < 100 && !Revoked(protector, underOldKey); attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        var run = await host.Provider.GetRequiredService<SecretReprotectionRound>().RunAsync(TestContext.Current.CancellationToken);

        run.Failed.Should().BeGreaterThanOrEqualTo(1);
        (await SeedAsync(connection, userId)).Should().Be(underOldKey, "a secret under a revoked key is re-enrolled, not carried over to a new key");
    }

    private static (string Plaintext, bool RequiresMigration) Unprotected(IPersistedDataProtector protector, string value)
    {
        var bytes = protector.DangerousUnprotect(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(value), false, out var requiresMigration, out _);
        return (System.Text.Encoding.UTF8.GetString(bytes), requiresMigration);
    }

    private static bool Revoked(IPersistedDataProtector protector, string value)
    {
        try
        {
            protector.DangerousUnprotect(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(value), false, out _, out _);
            return false;
        }
        catch (CryptographicException)
        {
            return true;
        }
    }

    private static async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, Guid id, string value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("value", value);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    private static async Task<string> ScalarAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", id);
        return (string)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (_entries)
                _entries.Add(formatter(state, exception) + " " + exception);
        }

        public void Dispose()
        {
        }
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
