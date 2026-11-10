using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
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
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-211 (the review's findings H1 and N1) against TimescaleDB, through the real migration job: an installation that holds data from
/// before ARV-211 (its key ring rows carry no row signature, script 0073) and is upgraded, or restored from such a backup, without the
/// upgrade window would leave every stored secret under keys the new hosts refuse. That never-upgraded case alone is refused: by the
/// migration job (api-main --migrate, the chart's pre-upgrade hook, exit code 2) and by the hosts at start (KeyRingUpgradeGate,
/// pointing at wiki 04 section 6.2), even when a host's own start already made a signed key that no value names. Once a stored value is
/// under a signed key, neither refuses any more: a value the re-protection could not move, and a planted unsigned stub key with a
/// value a runtime-login writer pointed at it, are leftovers, logged and measured (<c>ariva.keyring.problems</c>, outcome
/// <c>unsigned</c>), never a stopped host or release. An unsigned copy of a signed key counts for nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class KeyRingUpgradeTests(PostgresFixture fixture)
{
    #region Fields

    private const string RuntimeLogin = "it_key_ring_upgrade_app";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Tests

    [Fact]
    public async Task Upgrade_Should_StopOnlyTheNeverUpgradedCase_When_StoredSecretsNeedKeysFromBeforeArv211()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.KeyRingUpgrade);
        (await MigrateAsync(database, window: null)).Should().Be(0, "an empty database has nothing to read");
        using var wrapping = Wrapping();
        var settings = RuntimeSettings(database);
        using var metrics = new KeyAgeMetrics();
        using var reader = new InstrumentReader(metrics.Meter);

        // The installation before ARV-211: its hosts wrote the key ring without row signatures and protected two users' TOTP seeds.
        string first, second;
        await using (var installed = BeforeArv211Host(settings, wrapping))
        {
            var protector = installed.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose);
            first = protector.Protect("JBSWY3DPEHPK3PXP");
            second = protector.Protect("KRSXG5CTMVRXEZLU");
        }

        await InsertUserAsync(database, "key.ring.upgrade.a", first);
        await InsertUserAsync(database, "key.ring.upgrade.b", second);
        var signing = KeyRingSigning.From(Signer());
        // A host started without the window makes a key of its own, signed by the new signer, as Data Protection does at start; no
        // stored value names it, so the case stays the never-upgraded one.
        await using (var started = Host(settings, wrapping, signing))
            started.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        (await ScalarAsync<long>(Admin(database), "SELECT count(*) FROM data_protection_key WHERE signature IS NOT NULL")).Should().Be(1);

        // Never upgraded, no window: the migration job refuses (exit code 2), and so does a host at start, which measures the count.
        (await MigrateAsync(database, window: null)).Should().Be(2, "every stored seed needs a key the new hosts would refuse");
        var refused = () => Gate(settings, signing, metrics).StartAsync(Ct);
        (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("2 user TOTP seed(s)")
            .And.Contain("DataProtection:Signing:CertificateSignedUntil is not set").And.Contain("wiki 04 section 6.2");
        Unsigned(reader).Should().Be(2);

        // With the window open both go on, and the upgraded host moves the first seed to a key it signed, as SecretReprotection does;
        // the second stays where it was (a value the job could not move).
        var window = DateTimeOffset.UtcNow.AddDays(14);
        (await MigrateAsync(database, window: window.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Should().Be(0);
        var open = KeyRingSigning.From(signing.Current, certificateSignedUntil: window);
        await Gate(settings, open, metrics).StartAsync(Ct);
        string moved, third;
        await using (var upgraded = Host(settings, wrapping, open))
        {
            var protector = (IPersistedDataProtector)upgraded.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose);
            var plaintext = protector.DangerousUnprotect(WebEncoders.Base64UrlDecode(first), false, out var requiresMigration, out _);
            requiresMigration.Should().BeTrue("a key from before ARV-211 only unprotects");
            moved = WebEncoders.Base64UrlEncode(protector.Protect(plaintext));
            third = protector.Protect("GEZDGNBVGY3TQOJQ");
        }

        await ReplaceSeedAsync(database, first, moved);

        // The window removed with a value left behind: no host and no release stops; the leftover is measured.
        (await MigrateAsync(database, window: null)).Should().Be(0, "an installation already upgraded never stops a release on its leftovers");
        await Gate(settings, signing, metrics).StartAsync(Ct);
        Unsigned(reader).Should().Be(1, "the second seed still needs its key from before ARV-211");

        // A runtime-login writer (case b): an unsigned stub key and another user's seed pointed at it, beside unsigned copies of the
        // signed keys. The hosts and the release still go on; the count takes the stub's value, never the copies.
        await InsertUserAsync(database, "key.ring.upgrade.c", third);
        var stub = Guid.NewGuid();
        await AsRuntimeAsync(database, "INSERT INTO data_protection_key (friendly_name, xml) VALUES (@name, @xml)",
            ("name", $"key-{stub}"), ("xml", $"<key id=\"{stub}\" version=\"1\" />"));
        await AsRuntimeAsync(database, """UPDATE "user" SET totp_secret_protected = @value WHERE user_name = 'key.ring.upgrade.c'""",
            ("value", NamingPayload(stub)));
        await PlantUnsignedCopiesAsync(database);

        (await MigrateAsync(database, window: null)).Should().Be(0, "a planted row does not block a release");
        await Gate(settings, signing, metrics).StartAsync(Ct);
        Unsigned(reader).Should().Be(2, "the leftover and the value the writer pointed at the stub");
        var finding = await KeyRingUpgradeCheck.FindAsync(Admin(database), signing, Ct);
        finding.UserSeeds.Should().Be(2);
        finding.OnSignedKeys.Should().Be(1, "the moved seed is under a signed key; an unsigned copy of that key does not count");

        // Each value set again (the users re-enrol): nothing is left.
        await AsRuntimeAsync(database, """UPDATE "user" SET totp_secret_protected = NULL, totp_enrolled = false WHERE user_name IN ('key.ring.upgrade.b', 'key.ring.upgrade.c')""");
        await Gate(settings, signing, metrics).StartAsync(Ct);
        Unsigned(reader).Should().Be(0);
        (await KeyRingUpgradeCheck.FindAsync(Admin(database), Ct)).Total.Should().Be(0);
    }

    #endregion

    #region Helpers

    private string Admin(string database) => fixture.ConnectionString(database);

    private DatabaseSettings RuntimeSettings(string database) => new()
    {
        Host = fixture.Hostname,
        Port = fixture.Port,
        Name = database,
        Username = RuntimeLogin,
        Password = RuntimePassword
    };

    /// <summary>The migration job (api-main --migrate) with the release's window, as the chart's pre-upgrade hook runs it.</summary>
    private async Task<int> MigrateAsync(string database, string window)
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
        if (window is not null)
            settings["DataProtection:Signing:CertificateSignedUntil"] = window;
        return await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), Ct);
    }

    private static KeyRingUpgradeGate Gate(DatabaseSettings settings, KeyRingSigning signing, KeyAgeMetrics metrics) =>
        new(settings, signing, TimeProvider.System, NullLogger<KeyRingUpgradeGate>.Instance, metrics);

    /// <summary>The upgrade check's last count, as the hosts report it.</summary>
    private static double Unsigned(InstrumentReader reader) =>
        reader.Observe(KeyAgeMetrics.ProblemsName).Single(m => m.Tags["outcome"] == KeyAgeMetrics.Unsigned).Value;

    /// <summary>A statement as the hosts' runtime login runs it: what a writer who holds that login can do.</summary>
    private async Task AsRuntimeAsync(string database, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
        var runtime = new NpgsqlConnectionStringBuilder(Admin(database)) { Username = RuntimeLogin, Password = RuntimePassword }.ConnectionString;
        await using var connection = new NpgsqlConnection(runtime);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        (await command.ExecuteNonQueryAsync(Ct)).Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>A value laid out as a Data Protection payload (the magic 09 F0 C9 F0, the key id in .NET's byte order, then bytes) naming <paramref name="keyId"/>.</summary>
    private static string NamingPayload(Guid keyId)
    {
        byte[] payload = [0x09, 0xF0, 0xC9, 0xF0, .. keyId.ToByteArray(), .. RandomNumberGenerator.GetBytes(48)];
        return WebEncoders.Base64UrlEncode(payload);
    }

    /// <summary>Today's hosts against PostgreSQL as the runtime login, behind the trusted key filter (rows signed, script 0073).</summary>
    private static ServiceProvider Host(DatabaseSettings settings, X509Certificate2 wrapping, KeyRingSigning signing)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaDataProtection(settings, DataProtectionCertificates.From(wrapping), signing);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A host before ARV-211: the key ring straight in PostgreSQL (rows without a signature), each element signed with the wrapping
    /// certificate's own key.
    /// </summary>
    private static ServiceProvider BeforeArv211Host(DatabaseSettings settings, X509Certificate2 wrapping)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(DataProtectionCertificates.From(wrapping));
        // Only to read its own keys back while it writes them (the decryptor is today's, inside its window).
        services.AddSingleton(KeyRingSigning.From(Signer(), certificateSignedUntil: DateTimeOffset.UtcNow.AddDays(1)));
        services.AddDataProtection().SetApplicationName(DataProtectionExtensions.ApplicationName);
        services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = new PostgresXmlRepository(settings);
            options.XmlEncryptor = new CertificateSignedEncryptor(wrapping);
        });
        return services.BuildServiceProvider();
    }

    private sealed class CertificateSignedEncryptor(X509Certificate2 certificate) : IXmlEncryptor
    {
        public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
            new(OaepGcmXml.EncryptSignedWithCertificate(plaintextElement, certificate), typeof(OaepGcmXmlDecryptor));
    }

    private async Task InsertUserAsync(string database, string userName, string seed)
    {
        await using var connection = new NpgsqlConnection(Admin(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO "user" (id, user_name, password_hash, password_salt, password_algorithm, password_iterations, must_change_password, totp_enrolled,
                is_disabled, failed_login_count, is_break_glass, all_sites, totp_secret_protected)
            VALUES (gen_random_uuid(), @name, 'hash', 'salt', 'pbkdf2-sha256', 600000, false, true, false, 0, false, false, @seed)
            """, connection);
        command.Parameters.AddWithValue("name", userName);
        command.Parameters.AddWithValue("seed", seed);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
    }

    private async Task ReplaceSeedAsync(string database, string old, string value)
    {
        await using var connection = new NpgsqlConnection(Admin(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""UPDATE "user" SET totp_secret_protected = @value WHERE totp_secret_protected = @old""", connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("old", old);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
    }

    /// <summary>Every signed key row copied without its signature under another name, as a writer with INSERT could.</summary>
    private async Task PlantUnsignedCopiesAsync(string database)
    {
        await using var connection = new NpgsqlConnection(Admin(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO data_protection_key (friendly_name, xml) SELECT friendly_name || '-copy', xml FROM data_protection_key WHERE signature IS NOT NULL AND xml LIKE '<key %'",
            connection);
        (await command.ExecuteNonQueryAsync(Ct)).Should().BeGreaterThanOrEqualTo(1);
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
        using var created = new CertificateRequest("CN=Ariva DP (key ring upgrade)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 Signer()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ariva key ring signing (key ring upgrade)", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    #endregion
}
