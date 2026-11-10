using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Di;
using Ariva.Di.Extensions;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-207 against TimescaleDB, through the real migration job and the runtime login it creates, with keys the hosts' own Data
/// Protection wrote to <c>data_protection_key</c> (so script 0072's pattern is held to the shape Data Protection really writes):
/// <list type="bullet">
/// <item>the runtime login still cannot delete a key itself, nor ask the purge for less than 30 days;</item>
/// <item>the key lifecycle round deletes an expired key past its retention only once no stored value names it (a user's TOTP
/// seed, an integration client's seed and an outbound endpoint's secret, each naming a key of its own, the last two with a
/// header that ends in base64url's own characters), keeps a key expired within its retention, keeps every revocation, deletes
/// nothing while no unexpired key is in the ring, and reports when the oldest dated credential of each kind was made.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class KeyLifecycleTests(PostgresFixture fixture)
{
    #region Fields

    private const string RuntimeLogin = "it_key_lifecycle_app";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string _database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Tests

    [Fact]
    public async Task Round_Should_DeleteAnExpiredKeyOnlyOnceNoStoredValueNeedsIt_When_ItsRetentionHasPassed()
    {
        await ResetAsync();
        using var wrapping = Wrapping();
        var signing = KeyRingSigning.From(Signer());
        await using var host = Host(wrapping, signing);
        var keys = host.GetRequiredService<IKeyManager>();
        var seed = host.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose).Protect("JBSWY3DPEHPK3PXP");
        var first = KeyId(seed);
        // Two more keys that expired 100 days ago, each named by a value of its own column (the review's finding M3): an integration
        // client's seed and an outbound endpoint's secret, laid out as Data Protection lays a payload out, whose header ends in '-'
        // and '_' (base64url's own characters, which the purge translates before it decodes).
        var clientKey = keys.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-190), DateTimeOffset.UtcNow.AddDays(-100)).KeyId;
        var outboundKey = keys.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-190), DateTimeOffset.UtcNow.AddDays(-100)).KeyId;
        var clientValue = NamingPayload(clientKey, tail: 0x3E);
        var outboundValue = NamingPayload(outboundKey, tail: 0x3F);
        clientValue[27..28].Should().Be("-", "the header's last character comes from the tail byte 0x3E");
        outboundValue[27..28].Should().Be("_", "and 0x3F");
        KeyId(clientValue).Should().Be(clientKey);
        KeyId(outboundValue).Should().Be(outboundKey);
        await InsertUserAsync(seed);
        await InsertIntegrationClientAndEndpointAsync(clientValue, outboundValue);
        // A revocation of the first key, written as Data Protection writes one (by SQL, so the host's key ring is not refreshed).
        await InsertRevocationAsync(first);
        // The runtime login cannot change a key: the migration login ages the first key, expired 100 days ago.
        (await AgeKeyAsync(first, expiredDaysAgo: 100)).Should().Be(1);
        // A current key, as the hosts make once the first expired, and a key that expired 10 days ago (within its retention).
        keys.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        keys.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-100), DateTimeOffset.UtcNow.AddDays(-10));
        var round = Round();

        var held = await round.RunAsync(Ct);

        held.KeyRing.Deleted.Should().Be(0);
        held.KeyRing.Held.Should().Be(3, "each of the three columns names its own expired key, so each is kept past its retention");
        held.KeyRing.Kept.Should().Be(1, "the key that expired 10 days ago is within its retention");
        held.KeyRing.Active.Should().BeGreaterThanOrEqualTo(1, "the current key, as Data Protection wrote it, matches the function's pattern");
        var before = await KeyRowsAsync();
        before.Should().Contain([$"key-{first}", $"key-{clientKey}", $"key-{outboundKey}"]);
        // Kind names, held as booleans (ARV-249: the secret assertion check reads a name holding "Secret" as a secret).
        new[] { KeyAgeMetrics.IntegrationClientSecret, KeyAgeMetrics.IntegrationClientSeed, KeyAgeMetrics.OutboundSecret, KeyAgeMetrics.DataProtectionKey }
            .All(held.OldestMadeUtc.ContainsKey).Should().BeTrue("each kind a stored value names reports its oldest key");
        held.OldestMadeUtc[KeyAgeMetrics.IntegrationClientSecret].Should().BeCloseTo(DateTime.UtcNow.AddDays(-400), TimeSpan.FromMinutes(5));

        // Every value moves off its key (re-protected, or set again): nothing names the three any more.
        await ClearSeedsAsync();
        var purged = await round.RunAsync(Ct);

        purged.KeyRing.Deleted.Should().Be(3);
        purged.KeyRing.Held.Should().Be(0);
        purged.KeyRing.Kept.Should().Be(1);
        var rows = await KeyRowsAsync();
        string[] gone = [$"key-{first}", $"key-{clientKey}", $"key-{outboundKey}"];
        rows.Should().NotContain(gone, "a key past its retention that no stored value needs is destroyed");
        rows.Should().Contain($"revocation-{first}", "a revocation stays, so no copy of the key can become usable again");
        rows.Should().BeEquivalentTo(before.Where(r => !gone.Contains(r)), "nothing else is deleted");
        (await round.RunAsync(Ct)).KeyRing.Deleted.Should().Be(0, "a second run finds nothing more");
    }

    [Fact]
    public async Task Purge_Should_KeepEveryKey_When_NoUnexpiredKeyIsInTheRing()
    {
        await ResetAsync();
        using var wrapping = Wrapping();
        await using var host = Host(wrapping, KeyRingSigning.From(Signer()));
        var keys = host.GetRequiredService<IKeyManager>();
        keys.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-300), DateTimeOffset.UtcNow.AddDays(-200));
        keys.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-250), DateTimeOffset.UtcNow.AddDays(-150));

        var run = await Round().RunAsync(Ct);

        run.KeyRing.Deleted.Should().Be(0, "the ring is never left without a key Data Protection could fall back on");
        run.KeyRing.Held.Should().Be(2);
        run.KeyRing.Active.Should().Be(0);
        (await KeyRowsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task RuntimeLogin_Should_NeitherDeleteAKeyNorShortenTheRetention_When_ItTries()
    {
        await ResetAsync();

        var delete = async () => await ExecuteAsync(Runtime, "DELETE FROM data_protection_key");
        var truncate = async () => await ExecuteAsync(Runtime, "TRUNCATE data_protection_key");
        var shorter = async () => await ExecuteAsync(Runtime, "SELECT * FROM ariva_data_protection_key_purge(29, 100)");
        var larger = async () => await ExecuteAsync(Runtime, "SELECT * FROM ariva_data_protection_key_purge(30, 1001)");

        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await truncate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await shorter.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidParameterValue);
        (await larger.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidParameterValue);
        (await ScalarAsync<bool>(Admin, "SELECT has_function_privilege('public', 'ariva_data_protection_key_purge(integer,integer)', 'EXECUTE')"))
            .Should().BeFalse("only ariva_runtime may run it");
        (await ScalarAsync<bool>(Admin, "SELECT p.prosecdef FROM pg_proc p WHERE p.oid = 'ariva_data_protection_key_purge(integer,integer)'::regprocedure"))
            .Should().BeTrue("it runs as its owner, so the runtime role needs no DELETE");
    }

    #endregion

    #region Helpers

    private string Admin => fixture.ConnectionString(_database);

    private string Runtime => new NpgsqlConnectionStringBuilder(fixture.ConnectionString(_database)) { Username = RuntimeLogin, Password = RuntimePassword }.ConnectionString;

    private DatabaseSettings RuntimeSettings() => new()
    {
        Host = fixture.Hostname,
        Port = fixture.Port,
        Name = _database,
        Username = RuntimeLogin,
        Password = RuntimePassword
    };

    /// <summary>
    /// The class's database, migrated once through the migration job (api-main --migrate) with this class's runtime login, then
    /// emptied of what each test writes (as the migration login).
    /// </summary>
    private async Task ResetAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_database is null)
            {
                var database = await fixture.CreateDatabaseAsync(TestDatabase.KeyLifecycle);
                (await MigrateAsync(database)).Should().Be(0);
                _database = database;
            }
        }
        finally
        {
            Gate.Release();
        }

        await ExecuteAsync(Admin, """
            DELETE FROM data_protection_key;
            DELETE FROM "user" WHERE user_name = 'key.lifecycle';
            DELETE FROM integration_client WHERE client_id = 'ic_abcdefghijklmnopqrstuvwxyz';
            DELETE FROM outbound_endpoint WHERE code = 'key-lifecycle';
            """);
    }

    private async Task<int> MigrateAsync(string database)
    {
        var settings = new Dictionary<string, string>
        {
            ["Database:Host"] = fixture.Hostname,
            ["Database:Port"] = fixture.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Migration:Username"] = fixture.AdminUsername,
            ["Database:Migration:Password"] = fixture.AdminPassword,
            ["Database:Username"] = RuntimeLogin,
            ["Database:Password"] = RuntimePassword
        };
        return await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), Ct);
    }

    /// <summary>The hosts' Data Protection against PostgreSQL as the runtime login, behind the trusted key filter.</summary>
    private ServiceProvider Host(X509Certificate2 wrapping, KeyRingSigning signing)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaDataProtection(RuntimeSettings(), DataProtectionCertificates.From(wrapping), signing);
        return services.BuildServiceProvider();
    }

    private KeyLifecycleRound Round() => new(RuntimeSettings(), new KeyLifecycleSettings(), NullLogger<KeyLifecycleRound>.Instance);

    private static Guid KeyId(string payload) => new(WebEncoders.Base64UrlDecode(payload).AsSpan(4, 16));

    private async Task<int> AgeKeyAsync(Guid keyId, int expiredDaysAgo)
    {
        static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'", CultureInfo.InvariantCulture);
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            UPDATE data_protection_key
               SET xml = regexp_replace(regexp_replace(xml, '<activationDate>[^<]*</activationDate>', '<activationDate>' || @activation || '</activationDate>'),
                                        '<expirationDate>[^<]*</expirationDate>', '<expirationDate>' || @expiration || '</expirationDate>')
             WHERE friendly_name = @name
            """, connection);
        command.Parameters.AddWithValue("activation", Stamp(DateTimeOffset.UtcNow.AddDays(-expiredDaysAgo - 90)));
        command.Parameters.AddWithValue("expiration", Stamp(DateTimeOffset.UtcNow.AddDays(-expiredDaysAgo)));
        command.Parameters.AddWithValue("name", $"key-{keyId}");
        return await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task InsertUserAsync(string seed)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO "user" (id, user_name, password_hash, password_salt, password_algorithm, password_iterations, must_change_password, totp_enrolled,
                is_disabled, failed_login_count, is_break_glass, all_sites, totp_secret_protected)
            VALUES (gen_random_uuid(), 'key.lifecycle', 'hash', 'salt', 'pbkdf2-sha256', 600000, false, true, false, 0, false, false, @seed)
            """, connection);
        command.Parameters.AddWithValue("seed", seed);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
    }

    /// <summary>A client whose secret is 400 days old and an endpoint, with the given seed and secret.</summary>
    private async Task InsertIntegrationClientAndEndpointAsync(string clientSeed, string outboundSecret)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO integration_client (id, client_id, name, kind, status, scope_names, site_codes, allowed_networks, require_totp_per_request,
                secret_algorithm, secret_iterations, secret_salt, secret_hash, secret_changed_utc, totp_secret_protected, totp_changed_utc,
                tokens_valid_from_utc, token_version)
            VALUES (gen_random_uuid(), 'ic_abcdefghijklmnopqrstuvwxyz', 'Key lifecycle client', 'Other', 'Active', 'queues:read', 'AMM', '10.0.0.0/8', false,
                'pbkdf2-sha256', 600000, 'salt', 'hash', now() - interval '400 days', @seed, now() - interval '200 days', now(), 0);
            INSERT INTO outbound_endpoint (id, code, name, purpose, status, site_codes, base_url, allowed_networks, auth_kind, header_name,
                totp_per_request, timeout_seconds, retry_count, breaker_failures, break_seconds, poll_seconds, secret_protected,
                has_client_certificate, secret_changed_utc, client_version)
            VALUES (gen_random_uuid(), 'key-lifecycle', 'Key lifecycle partner', 'Generic', 'Active', 'AMM', 'https://partner.example/', '10.0.0.0/8', 'ApiKeyHeader', 'X-Api-Key',
                false, 10, 0, 5, 30, 0, @secret, false, now() - interval '50 days', 0);
            """, connection);
        command.Parameters.AddWithValue("seed", clientSeed);
        command.Parameters.AddWithValue("secret", outboundSecret);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(2);
    }

    /// <summary>
    /// A value laid out as a Data Protection payload is (the magic 09 F0 C9 F0, the key id in .NET's byte order, then ciphertext),
    /// naming <paramref name="keyId"/>, whose 21st byte is <paramref name="tail"/>: the low six bits of that byte are the header's
    /// 28th character, so 0x3E gives '-' and 0x3F gives '_'.
    /// </summary>
    private static string NamingPayload(Guid keyId, byte tail)
    {
        byte[] payload = [0x09, 0xF0, 0xC9, 0xF0, .. keyId.ToByteArray(), tail, .. RandomNumberGenerator.GetBytes(48)];
        return WebEncoders.Base64UrlEncode(payload);
    }

    private async Task InsertRevocationAsync(Guid keyId)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("INSERT INTO data_protection_key (friendly_name, xml) VALUES (@name, @xml)", connection);
        command.Parameters.AddWithValue("name", $"revocation-{keyId}");
        command.Parameters.AddWithValue("xml", string.Create(CultureInfo.InvariantCulture,
            $"<revocation version=\"1\"><revocationDate>{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'+00:00'}</revocationDate><key id=\"{keyId}\" /><reason>ARV-207 test</reason></revocation>"));
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
    }

    private Task ClearSeedsAsync() => ExecuteAsync(Admin, """
        UPDATE "user" SET totp_secret_protected = NULL, totp_enrolled = false;
        UPDATE integration_client SET totp_secret_protected = 'not-a-payload' WHERE client_id = 'ic_abcdefghijklmnopqrstuvwxyz';
        UPDATE outbound_endpoint SET secret_protected = 'not-a-payload' WHERE code = 'key-lifecycle';
        """);

    private async Task<List<string>> KeyRowsAsync()
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT friendly_name FROM data_protection_key ORDER BY friendly_name", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
            rows.Add(reader.GetString(0));
        return rows;
    }

    private static async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)await command.ExecuteScalarAsync(Ct);
    }

    private static X509Certificate2 Wrapping()
    {
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        using var created = new CertificateRequest("CN=Ariva DP (key lifecycle)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 Signer()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ariva key ring signing (key lifecycle)", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    #endregion
}
