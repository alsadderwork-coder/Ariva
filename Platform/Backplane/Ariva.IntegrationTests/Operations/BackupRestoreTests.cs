using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Integration;
using Ariva.Core.Services;
using Ariva.Core.Services.Integration;
using Ariva.Di;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Integration;
using Ariva.Infra.Messaging;
using Ariva.Infra.Security;
using Ariva.Infra.Timescale;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// ARV-126: a backup restored into a fresh server gives back a working deployment. Two servers are started as the
/// TimescaleDB chart starts one (<see cref="ChartDatabase"/>). The source gets the release's migration job and
/// Ariva.Api.Main as a cluster runs it (k8s-dev, runtime login, the Data Protection certificate from PEM files, the demo
/// site), an administrator with an authenticator, an integration client, an outbound endpoint with a secret, time series
/// (a compressed chunk included), stream positions and sent events of compacted topics. scripts/ops/ariva-db.sh takes the
/// backup in the source and restores it in the other server, as an operator runs it in the pod. The backup holds no secret
/// in plain text and no role. After the migration job (with another runtime password), the runtime login holds the
/// runtime role only and cannot change the schema; Ariva.Api.Main and Ariva.Api.Integration start on the restored database
/// with the same certificate and verify schema_version, the administrator signs in with a code from the restored seed and
/// reads the site exactly as before, the integration client exchanges its credentials (its restored seed) and the outbound
/// secret reads back. The key ring check names the certificate the keys need, and after a Kafka loss the newest event of
/// every compacted key is due again and the stream positions are cleared. Last, the check after a restore fails on what a
/// forged dump could leave (a C or SECURITY DEFINER function, an event trigger, an extra member of the runtime role, a
/// migration login given replication). The operator wrapper's signed path (the manifest's signature, the private copy and
/// every refusal before kubectl) is proven by <c>node scripts/ops/ariva-ops.mjs --self-test</c>.
/// </summary>
public sealed class BackupRestoreTests : IAsyncLifetime
{
    private const string RuntimeLogin = "ariva_app";
    private const string AdminUserName = "it.restore.admin";
    private const string DumpPath = ChartDatabase.BackupDirectory + "/ariva.dump";
    private const string EndpointCode = "restore-partner";

    private static readonly string SourceRuntimePassword = "rt-" + Hex(12);
    private static readonly string RestoredRuntimePassword = "rt2-" + Hex(12);
    private static readonly string AdminPassword = "restore lantern " + Hex(6);
    private static readonly string OutboundApiKey = "partner-key-" + Hex(16);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-restore-" + Guid.NewGuid().ToString("N"));
    private string _backupDirectory;
    private string _certificatePath;
    private string _keyPath;
    private string _certificateThumbprint;
    private string _privateKeyPem;
    private ChartDatabase _source;
    private ChartDatabase _target;

    public async ValueTask InitializeAsync()
    {
        // The folder both servers see as /backup: the database user (uid 1000) writes the dump there.
        _backupDirectory = Path.Combine(_workDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);
        File.SetUnixFileMode(_backupDirectory, (UnixFileMode)0b111_111_111);

        // The ariva-dataprotection secret as the chart mounts it: an RSA 3072 certificate and its key in PEM, readable by the
        // host only. It lives apart from the database and from every backup.
        var secrets = Path.Combine(_workDirectory, "dataprotection");
        Directory.CreateDirectory(secrets);
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        var request = new CertificateRequest("CN=Ariva Data Protection (restore test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _certificatePath = Path.Combine(secrets, "tls.crt");
        _keyPath = Path.Combine(secrets, "tls.key");
        _privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        await File.WriteAllTextAsync(_certificatePath, certificate.ExportCertificatePem(), Ct);
        await File.WriteAllTextAsync(_keyPath, _privateKeyPem, Ct);
        File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _certificateThumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

        var servers = await Task.WhenAll(ChartDatabase.StartAsync(_backupDirectory, Ct), ChartDatabase.StartAsync(_backupDirectory, Ct));
        (_source, _target) = (servers[0], servers[1]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    [Fact(Timeout = 900_000)]
    public async Task Restore_Should_BringBackTheSiteWithItsSecretsReadable_When_ABackupIsRestoredIntoAFreshServer()
    {
        // The source deployment: the release's migration job, then Ariva.Api.Main as the cluster runs it, with the demo site.
        (await DatabaseMigration.RunAsync(Configuration(_source.MigrationSettings(RuntimeLogin, SourceRuntimePassword)), Ct)).Should().Be(0);
        var source = await SeedSourceAsync();

        // The backup, taken by the operations script in the database pod as the migration login, while Main is running.
        var manifest = await _source.OpsAsync(Ct, "manifest");
        manifest.ExitCode.Should().Be(0, manifest.Stderr);
        using (var json = JsonDocument.Parse(manifest.Stdout))
        {
            json.RootElement.GetProperty("format").GetString().Should().Be("ariva-backup-manifest/1");
            json.RootElement.GetProperty("scripts").GetInt32().Should().Be(SqlScriptCatalog.Embedded().Count);
            json.RootElement.GetProperty("keyRingCertificates").EnumerateArray().Select(e => e.GetString()).Should().Equal(_certificateThumbprint);
        }

        var backup = await _source.OpsAsync(Ct, "backup", "--output", DumpPath);
        backup.ExitCode.Should().Be(0, backup.Stderr);
        AssertNoSecretIn(backup.Stdout + backup.Stderr, source, "the backup's own output");
        (await _source.ExecAsync(Ct, "chmod", "0644", DumpPath)).ExitCode.Should().Be(0, "the test reads the dump the database user wrote (0600)");
        await AssertNoPlaintextSecretAsync(source);

        // A dump of another deployment, or one whose objects belong to another login, is refused before anything changes.
        var foreign = await _target.OpsAsync(Ct, "check-dump", "--input", DumpPath, "--login", "someone_else");
        foreign.ExitCode.Should().NotBe(0);
        foreign.Stderr.Should().Contain("not the migration login someone_else");

        // The restore into the fresh server, then the release's migration job with this deployment's own runtime password.
        var restore = await _target.OpsAsync(Ct, "restore", "--input", DumpPath);
        restore.ExitCode.Should().Be(0, restore.Stderr + restore.Stdout);
        restore.Stdout.Should().Contain("every object belongs to ariva")
            .And.Contain("ariva_runtime and the logins that hold it have no CREATE on public, on any other schema or on the database");
        // ARV-183 and its review: the wider checks pass on a clean restore (every Ariva function body is one its scripts ship, and
        // the system functions are template1's).
        foreach (var line in new[] { "logins", "held roles", "role settings", "settings", "rules", "other functions", "plpgsql", "system functions", "replication", "planted",
                     "internal privileges", "runtime grants", "default privileges" })
            restore.Stdout.Should().Contain($"ok    {line}|");
        // The backup is at the latest script, so restore's own check compares the runtime role's privileges and the views (an ok
        // "not compared" would be the wrong pass here).
        restore.Stdout.Should().Contain("ok    runtime privileges|ariva_runtime's grants on public as the scripts leave them")
            .And.Contain("ok    views|the views of public are the scripts' 3, each reading what its script reads");
        var again = await _target.OpsAsync(Ct, "restore", "--input", DumpPath);
        again.ExitCode.Should().NotBe(0, "a restore goes into an empty database only");
        again.Stderr.Should().Contain("is not empty");
        (await DatabaseMigration.RunAsync(Configuration(_target.MigrationSettings(RuntimeLogin, RestoredRuntimePassword)), Ct)).Should().Be(0);
        await AssertRestoredPrivilegesAsync();
        await AssertRestoredDataAsync(source);

        // The hosts on the restored database, with the same certificate.
        await using (var main = new DeployedHost<Ariva.Api.Main._IAssemblyMark>(HostSettings(_target, RestoredRuntimePassword)))
        {
            var client = main.Client();
            var token = await SignInAsync(client, source.TotpSeed, 0);
            (await GetAsync(client, token, "api/v1/admin/airports")).Should().Be(source.Airports, "the restored site is served as before");
            (await GetAsync(client, token, "api/v1/admin/zone-profiles?siteCode=DMO")).Should().Be(source.ZoneProfiles);
        }

        await using (var integration = new DeployedHost<Ariva.Api.Integration._IAssemblyMark>(HostSettings(_target, RestoredRuntimePassword)))
        {
            await using var scope = integration.Services.CreateAsyncScope();
            var exchange = await scope.ServiceProvider.GetRequiredService<ISvcIntegrationAuth>().ExchangeAsync(
                new IntegrationTokenRequest(source.Client.Client.ClientId, source.Client.ClientSecret, Totp.Code(Base32.Decode(source.Client.TotpSecret), Totp.StepAt(DateTimeOffset.UtcNow))),
                IPAddress.Parse("10.20.0.7"), Ct);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(Ct);
            exchange.HasErrors.Should().BeFalse("the client's restored TOTP seed decrypts: " + string.Join("; ", exchange.ErrorMessages));
            var stored = await ScalarAsync<string>(_target.ConnectionString(RuntimeLogin, RestoredRuntimePassword), Query.OutboundSecret);
            string.Equals(integration.Services.GetRequiredService<OutboundSecrets>().Read(stored).ApiKey, OutboundApiKey, StringComparison.Ordinal).Should().BeTrue("the restored outbound secret decrypts to its API key");
        }

        // The key ring: every unexpired key is wrapped under the certificate the operator keeps; another one is not enough.
        var keyring = await _target.OpsAsync(Ct, "keyring", "--certificate-sha256", _certificateThumbprint);
        keyring.ExitCode.Should().Be(0, keyring.Stderr);
        keyring.Stdout.Should().Contain("covered ").And.Contain("certificate " + _certificateThumbprint);
        var wrongCertificate = await _target.OpsAsync(Ct, "keyring", "--certificate-sha256", new string('A', 64));
        wrongCertificate.ExitCode.Should().NotBe(0);
        wrongCertificate.Stdout.Should().Contain("FAIL missing");

        await AssertRecoveryAfterKafkaLossAsync();
        await AssertHostsVerifySchemaVersionAsync();
        await AssertCheckFindsWhatAForgedDumpWouldLeaveAsync();
    }

    #region Source deployment

    private sealed record SourceDeployment(string TotpSeed, IntegrationClientCredentialsViewModel Client, string Airports, string ZoneProfiles, string ProtectedSeed);

    private async Task<SourceDeployment> SeedSourceAsync()
    {
        var settings = HostSettings(_source, SourceRuntimePassword);
        settings["Seed:DemoTopology"] = "true";
        await using var main = new DeployedHost<Ariva.Api.Main._IAssemblyMark>(settings);
        var services = main.Services;
        var seed = Base32.Encode(Totp.NewSecret());

        // The demo seed runs in the background once Main has started: DMO, then AUH-TA, each with its published profile.
        var runtime = _source.ConnectionString(RuntimeLogin, SourceRuntimePassword);
        var until = DateTime.UtcNow.AddMinutes(3);
        while (await ScalarAsync<long>(runtime, Query.PublishedDemoSites) < 2)
        {
            DateTime.UtcNow.Should().BeBefore(until, "the demo seed publishes both sites' profiles");
            await Task.Delay(500, Ct);
        }

        // An administrator with an authenticator, through the entity and the host's own Data Protection (its key ring is now
        // in the database, wrapped under the certificate).
        Guid adminId;
        await using (var scope = services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentUser>().SetSystemUser(Guid.Empty, "it-restore-seed");
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var storage = unitOfWork.StorageProvider;
            await storage.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var user = new User(AdminUserName, "Restore administrator", null);
            user.SetPassword(PasswordHasher.Hash(AdminPassword), temporary: false);
            user.SetSites(true, [], null, DateTime.UtcNow);
            user.BeginTotpEnrolment(scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose).Protect(seed));
            user.ConfirmTotp(Totp.StepAt(DateTimeOffset.UtcNow) - 10);
            await storage.SaveAsync(user, Ct);
            user.Grant(RoleCodes.SystemAdministrator);
            await storage.SaveAsync(user.Roles.Last(), Ct);
            unitOfWork.PromiseToCommit();
            await unitOfWork.EndAsync(Ct);
            adminId = user.Id.Value;
        }

        // An integration client and an outbound endpoint, registered by that administrator through the services.
        IntegrationClientCredentialsViewModel client;
        await using (var scope = services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentUser>().SetSystemUser(adminId, AdminUserName);
            var created = await scope.ServiceProvider.GetRequiredService<ISvcIntegrationClients>().CreateAsync(
                new CreateIntegrationClientRequest("Restore AODB", "Aodb", [IntegrationScopes.FlightsWrite], ["DMO"], ["10.0.0.0/8"], false), Ct);
            var endpoint = await scope.ServiceProvider.GetRequiredService<ISvcOutboundEndpoints>().CreateAsync(new CreateOutboundEndpointRequest(
                EndpointCode, "Restore partner", "Generic", ["DMO"],
                new OutboundConnectionRequest("https://partner.example.test/", ["10.0.0.0/8"], "ApiKeyHeader", HeaderName: "X-Api-Key", PollSeconds: 30, RetryCount: 0),
                new OutboundSecretRequest(ApiKey: OutboundApiKey)), Ct);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(Ct);
            created.HasErrors.Should().BeFalse(string.Join("; ", created.ErrorMessages));
            endpoint.HasErrors.Should().BeFalse(string.Join("; ", endpoint.ErrorMessages));
            client = created.Data;
        }

        // What the site serves before the backup, read as the administrator over HTTP (a code of the previous step, so the
        // restored host's sign-in can use the current one: the replay guard keeps the last step used).
        var http = main.Client();
        var token = await SignInAsync(http, seed, -1);
        var airports = await GetAsync(http, token, "api/v1/admin/airports");
        var profiles = await GetAsync(http, token, "api/v1/admin/zone-profiles?siteCode=DMO");
        airports.Should().Contain("DMO");

        // Time series, a compressed chunk, a stream position and sent events of compacted topics, as the migration login.
        await ExecuteAsync(_source.ConnectionString(ChartDatabase.MigrationLogin, _source.MigrationPassword), Query.SeedTimeSeriesAndOutbox);
        var protectedSeed = await ScalarAsync<string>(runtime, Query.AdminSeed);
        return new SourceDeployment(seed, client, airports, profiles, protectedSeed);
    }

    #endregion

    #region Checks

    /// <summary>
    /// CWE-532 and the story's criterion: neither the dump nor its plain SQL (table data is compressed in the dump, so the
    /// plain form is what proves it) holds a password, a TOTP seed, a client or outbound secret, the certificate's private
    /// key, an unwrapped Data Protection key or a role; what is protected is there only in its protected form.
    /// </summary>
    private async Task AssertNoPlaintextSecretAsync(SourceDeployment source)
    {
        var raw = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(Path.Combine(_backupDirectory, "ariva.dump"), Ct));
        var plain = await _source.ExecAsync(Ct, "pg_restore", "--file=-", DumpPath);
        plain.ExitCode.Should().Be(0, plain.Stderr);
        plain.Stdout.Length.Should().BeGreaterThan(100_000, "the plain form of the whole dump");

        AssertNoSecretIn(raw, source, "the dump file");
        AssertNoSecretIn(plain.Stdout, source, "the dump's plain SQL");
        plain.Stdout.Should().NotContain("<masterKey", "no Data Protection key is stored unwrapped");
        plain.Stdout.Should().Contain(OaepGcmXml.Algorithm, "the key ring travels in the dump, wrapped under the certificate");
        plain.Stdout.Should().Contain(source.ProtectedSeed, "the seed travels only in its protected form");
        // Roles are cluster objects: no statement creates or changes one (the ensure functions' bodies only name them).
        System.Text.RegularExpressions.Regex.IsMatch(plain.Stdout, "(?m)^(CREATE|ALTER) ROLE ").Should().BeFalse("roles are not in a backup");
        plain.Stdout.Should().NotContain("SCRAM-SHA-256$", "no password verifier is in a backup");
    }

    private void AssertNoSecretIn(string text, SourceDeployment source, string what)
    {
        // A slice of the private key's base64 body: any copy of the key would contain it.
        var keyBody = _privateKeyPem.Split('\n')[3].Trim();
        foreach (var (name, secret) in new (string, string)[]
                 {
                     ("the administrator's TOTP seed", source.TotpSeed),
                     ("the administrator's password", AdminPassword),
                     ("the integration client's secret", source.Client.ClientSecret),
                     ("the integration client's TOTP seed", source.Client.TotpSecret.TrimEnd('=')),
                     ("the outbound API key", OutboundApiKey),
                     ("the runtime password", SourceRuntimePassword),
                     ("the migration password", _source.MigrationPassword),
                     ("the superuser password", _source.SuperuserPassword),
                     ("the Data Protection private key", keyBody)
                 })
        {
            text.Contains(secret, StringComparison.Ordinal).Should().BeFalse($"{what} must not hold {name}");
        }
    }

    private async Task AssertRestoredPrivilegesAsync()
    {
        var check = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        check.ExitCode.Should().Be(0, check.Stdout + check.Stderr);
        check.Stdout.Should().Contain("ariva_app holds ariva_runtime only (DML)");
        // ARV-189: the runtime role's privileges as ARIVA_RUNTIME_PRIVILEGES lists them, compared since the database is at the
        // list's script (a script that changes them without the list fails here; the line above it names each difference).
        check.Stdout.Should().Contain("ok    runtime privileges|ariva_runtime's grants on public as the scripts leave them")
            .And.Contain("ok    views|the views of public are the scripts' 3, each reading what its script reads")
            .And.Contain("ok    internal privileges|ariva_runtime: each inheritance child no wider than its parent")
            .And.Contain("ok    runtime grants|every grant of a relation or column to the runtime role goes to ariva_runtime without grant option")
            .And.Contain("ok    default privileges|script 0001's only");
        // TimescaleDB's own functions and event triggers are extension members: a clean restore has nothing to report.
        check.Stdout.Should().Contain("functions|no C, untrusted-language or SECURITY DEFINER function outside the extensions")
            .And.Contain("event triggers|none outside the extensions").And.Contain("group members|only the migration login and the named logins")
            .And.Contain("languages|plpgsql only").And.Contain("extensions|plpgsql, timescaledb and timescaledb_toolkit only");

        // The runtime login: this deployment's password, never the source's, and no DDL.
        var runtime = _target.ConnectionString(RuntimeLogin, RestoredRuntimePassword);
        var ddl = async () => await ExecuteAsync(runtime, Query.CreateTable);
        (await ddl.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        var oldPassword = async () => await ExecuteAsync(_target.ConnectionString(RuntimeLogin, SourceRuntimePassword), Query.One);
        (await oldPassword.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidPassword);
        (await ScalarAsync<string>(runtime, Query.RuntimeMemberships)).Should().Be("ariva_runtime");
        (await ScalarAsync<bool>(runtime, Query.IsSuperuser)).Should().BeFalse();
    }

    private async Task AssertRestoredDataAsync(SourceDeployment source)
    {
        var before = _source.ConnectionString(ChartDatabase.MigrationLogin, _source.MigrationPassword);
        var after = _target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword);
        foreach (var query in new[] { Query.QueueMinutes, Query.SensingEvents, Query.CompressedChunks, Query.Hypertables, Query.Jobs, Query.Keys, Query.Outbox })
            (await ScalarAsync<long>(after, query)).Should().Be(await ScalarAsync<long>(before, query), $"{query} is restored");
        (await ScalarAsync<long>(after, Query.CompressedChunks)).Should().BeGreaterThan(0, "a compressed chunk was in the backup");
        (await ScalarAsync<string>(after, Query.AdminSeed)).Should().Be(source.ProtectedSeed);
    }

    /// <summary>After a Kafka loss: the newest event of each key of the compacted topics is due again, nothing else; positions cleared.</summary>
    private async Task AssertRecoveryAfterKafkaLossAsync()
    {
        var owner = _target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword);
        (await ScalarAsync<long>(owner, Query.Unsent)).Should().Be(0, "the source marked every event sent");
        var expected = await ScalarAsync<string>(owner, Query.NewestCompacted);
        expected.Split(',').Should().HaveCountGreaterThanOrEqualTo(3, "the demo sites' published profiles and the device's newest event");

        var unconfirmed = await _target.OpsAsync(Ct, "after-kafka-loss");
        unconfirmed.ExitCode.Should().NotBe(0, "the stream worker must be stopped first");
        var dry = await _target.OpsAsync(Ct, "after-kafka-loss", "--confirm-stream-stopped", "--dry-run");
        dry.ExitCode.Should().Be(0, dry.Stderr);
        (await ScalarAsync<long>(owner, Query.Unsent)).Should().Be(0, "a dry run changes nothing");
        (await ScalarAsync<long>(owner, Query.StreamOffsets)).Should().Be(1);

        var recovery = await _target.OpsAsync(Ct, "after-kafka-loss", "--confirm-stream-stopped");
        recovery.ExitCode.Should().Be(0, recovery.Stderr);
        recovery.Stdout.Should().Contain("republish ariva.topology.zone-profile-activated.v1").And.Contain("stream_offset cleared 1");
        (await ScalarAsync<string>(owner, Query.UnsentSeqs)).Should().Be(expected, "exactly the newest row of each compacted key is due again");
        (await ScalarAsync<long>(owner, Query.StreamOffsets)).Should().Be(0);
    }

    /// <summary>The start check is on: a database that lacks a shipped script stops a host at its start.</summary>
    private async Task AssertHostsVerifySchemaVersionAsync()
    {
        await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), Query.ForgetLatestScript);
        await using var main = new DeployedHost<Ariva.Api.Main._IAssemblyMark>(HostSettings(_target, RestoredRuntimePassword));
        var start = () => main.Services;
        start.Should().Throw<SchemaBehindException>();
    }

    /// <summary>
    /// Defence in depth behind the signed manifest (ARV-126 review): `ariva-db.sh check` fails on what a forged dump run by
    /// the superuser could leave: a C function, a SECURITY DEFINER function (and, ARV-111a, the retention purge granted to
    /// PUBLIC, or with another body), an event trigger, an extra member of Ariva's runtime role, and a migration login given
    /// replication; the purge passes again once its grants are the shipped ones. ARV-183: also a privileged login, an ALTER
    /// SYSTEM setting, a rule, a function outside the extensions (in pg_catalog, and a plain one in public) and an extension
    /// of the server's own, which --allow-extension lets pass. Its review: roles with each attribute and a login that reaches a
    /// superuser or a server role through another role, Ariva's runtime role given a server role, a role's own setting, an
    /// Ariva function with another body or owner, a function added to plpgsql, a system function replaced in place, a
    /// publication and a subscription, and what --allow-login and --allow-setting let pass; a superuser read that fails is a
    /// FAIL; and a search path the migration login sets for the database, with an aggregate and an operator under system names
    /// that would make a superuser of it, is reported, the superuser reads nothing, and the login stays as it was. ARV-189: a lost
    /// revoke (report_delivery's table-wide UPDATE) and a grant on a chunk of the sensing events; and the purge with EXECUTE given
    /// with grant option, given to another role, or with another setting, each reported and passing again once put back. Its
    /// review: MAINTAIN on a hypertable, a view the scripts do not create, a grant on a compressed chunk, a continuous aggregate's
    /// table and a view outside public, an inheritance child of a table of public, a sequence the runtime role may set, an
    /// extension table granted to PUBLIC and a table granted to the runtime login rather than the role; and a database behind
    /// the list fails a plain check. Its second round: a view in information_schema, a view under a chunk's compressed name, a
    /// view the forged catalog names as a continuous aggregate's direct view that reads the shadow nowcast, a column of an
    /// extension table, a grant with grant option, a PUBLIC grant, and CREATE on the database. Its third round: default
    /// privileges for every schema and for _timescaledb_internal, a view over a toast table, a system table granted to PUBLIC
    /// and another to the runtime role, and CREATE on a schema for the runtime login. Its fourth: script 0001's own default
    /// privileges widened (TRUNCATE on public's tables, UPDATE with grant option on its sequences), and PUBLIC SELECT on an
    /// information_schema view that initdb leaves to its owner (_pg_user_mappings).
    /// </summary>
    private async Task AssertCheckFindsWhatAForgedDumpWouldLeaveAsync()
    {
        await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), Query.PlantAsMigrationLogin);
        await ExecuteAsync(_target.ConnectionString("postgres", _target.SuperuserPassword), Query.PlantAsSuperuser);
        // ALTER SYSTEM cannot run in a transaction block, so on its own (one statement, no implicit block).
        await ExecuteAsync(_target.ConnectionString("postgres", _target.SuperuserPassword), Query.AlterSystem);
        // CREATE SUBSCRIPTION on its own as well (no slot is created without a connection).
        await ExecuteAsync(_target.ConnectionString("postgres", _target.SuperuserPassword), Query.Subscription);

        var check = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        check.ExitCode.Should().NotBe(0, check.Stdout);
        check.Stdout.Should().Contain("FAIL  functions|").And.Contain("public.it_c_function (c)").And.Contain("public.it_definer (sql, SECURITY DEFINER)");
        // ARV-111a: Ariva's own SECURITY DEFINER function passes only with the grants and the body script 0054 ships: granted to
        // PUBLIC it is reported; with its grants back it passes; with another body (same settings and grants) it is reported again.
        check.Stdout.Should().Contain("public.ariva_retention_purge (plpgsql, SECURITY DEFINER)", "EXECUTE granted to PUBLIC");
        check.Stdout.Should().Contain("FAIL  event triggers|it_event");
        check.Stdout.Should().Contain("FAIL  group members|ariva_runtime has member it_extra_member");
        Line(check, "FAIL  logins|").Should().Contain("it_privileged (createrole)").And.Contain("it_super (superuser)").And.Contain("it_back (holds it_super)")
            .And.Contain("it_repl (replication)").And.Contain("it_bypass (bypassrls)").And.Contain("ariva_runtime (holds pg_execute_server_program)")
            .And.Contain($"{RuntimeLogin} (holds pg_execute_server_program)", "the runtime login reaches the server role through ariva_runtime");
        check.Stdout.Should().Contain("FAIL  held roles|ariva_runtime holds pg_execute_server_program");
        check.Stdout.Should().Contain("FAIL  role settings|it_back work_mem");
        check.Stdout.Should().Contain("FAIL  settings|ALTER SYSTEM set work_mem (postgresql.auto.conf)");
        check.Stdout.Should().Contain("FAIL  rules|public.site it_rule");
        Line(check, "FAIL  other functions|").Should().Contain("pg_catalog.it_shadow").And.Contain("public.it_plain")
            .And.Contain("public.validation_row_immutable", "an Ariva name with a body no script ships")
            .And.Contain("public.zone_profile_immutable", "an Ariva name owned by another role");
        check.Stdout.Should().Contain("FAIL  plpgsql|also holds function it_hidden()");
        check.Stdout.Should().Contain("FAIL  system functions|reverse differ from template1's");
        check.Stdout.Should().Contain("FAIL  replication|publication it_pub, subscription it_sub");
        // ARV-189: a lost revoke (report_delivery's table-wide UPDATE back, which script 0054 takes to four columns) and a grant on
        // a chunk, which reaches the append-only sensing events by the chunk's name. The database lacks its latest script here
        // (AssertHostsVerifySchemaVersionAsync forgot it), so the runtime role's privileges and the views are not compared until
        // the script is recorded again, and a plain check fails on that (only restore's own check, whose migration job follows,
        // passes it); what lies outside public is checked either way.
        check.Stdout.Should().Contain("FAIL  runtime privileges|not compared: the database is at ", check.Stdout)
            .And.Contain("FAIL  views|not compared: the database is behind the list");
        Line(check, "FAIL  internal privileges|").Should().Contain("_timescaledb_internal._hyper_")
            .And.Contain("_compressed", "a compressed chunk no wider than its hypertable either")
            .And.Contain("_timescaledb_internal._materialized_hypertable_", "a continuous aggregate's table at most SELECT")
            .And.Contain("it_side.it_view", "nothing on a relation outside public")
            .And.Contain("it_side.it_child (inherits public.alert_rule)")
            .And.Contain("public.outbox_message_seq_seq (sets values)")
            .And.Contain("_timescaledb_catalog.hypertable (extension table)", "PUBLIC given more than the extension gave");
        // Its second round: each relation judged by what it is. A view in information_schema, a view under the compressed name
        // of an uncompressed chunk, and a view the catalog names as a continuous aggregate's direct view, each reading what the
        // runtime role may not write or read, are reported by what they read; so is a column of an extension table.
        Line(check, "FAIL  internal privileges|").Should().Contain("information_schema.it_view, ", "any grant on a relation created after initdb, information_schema included")
            .And.Contain("information_schema.it_view (reads public.retention_period)")
            .And.Contain("_compressed (reads public.retention_period)")
            .And.Contain("_timescaledb_internal._direct_view_999 (reads public.queue_minute_shadow)")
            .And.Contain("_timescaledb_catalog.bgw_job (config) (extension table)")
            .And.Contain("_timescaledb_internal._direct_view_998 (reads pg_toast.pg_toast_", "a view over a toast table reads the rows behind its table")
            .And.Contain("pg_statistic (system table)", "PUBLIC given more than initdb gave")
            .And.Contain("pg_authid (system table)")
            .And.Contain("information_schema._pg_user_mappings (system table)", "initdb gives PUBLIC no SELECT on information_schema's _pg_ views");
        // Each entry lists its grants in byte order, the owner's own (ariva=...) first for every schema; script 0001's own
        // entries widened are reported as well as entries of their own.
        Line(check, "FAIL  default privileges|").Should().Contain("ariva in every schema on tables gives ").And.Contain("ariva_runtime=TRIGGER")
            .And.Contain("ariva in _timescaledb_internal on tables gives ariva_runtime=DELETE,ariva_runtime=INSERT,ariva_runtime=SELECT,ariva_runtime=UPDATE")
            .And.Contain("ariva in public on tables gives ariva_runtime=DELETE,ariva_runtime=INSERT,ariva_runtime=SELECT,ariva_runtime=TRUNCATE,ariva_runtime=UPDATE")
            .And.Contain("ariva in public on sequences gives ariva_runtime=SELECT,ariva_runtime=UPDATE with grant option,ariva_runtime=USAGE");
        Line(check, "FAIL  runtime grants|").Should().Contain($"public.integration_call to {RuntimeLogin}")
            .And.Contain("public.audit_entry to ariva_runtime with grant option").And.Contain("public.site to PUBLIC");
        Line(check, "FAIL  runtime role|may create objects: ").Should().Contain("ariva_runtime in the database").And.Contain("ariva_app in schema it_side");
        await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), Query.RememberLatestScript);
        var compared = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        compared.Stdout.Should().Contain("FAIL  runtime privileges|", compared.Stdout);
        Line(compared, "FAIL  runtime privileges|").Should().Contain(
            "report_delivery (the scripts: SELECT,INSERT,UPDATE(attempts reason sent_utc status); found: SELECT,INSERT,UPDATE)")
            .And.Contain("sensing_event (the scripts: SELECT,INSERT; found: SELECT,INSERT,MAINTAIN)");
        Line(compared, "FAIL  views|").Should().Contain("it_deliveries (the scripts: no such view; found: reads report_delivery)");
        check.Stdout.Should().Contain("ok    planted|", "nothing under a system name outside the extensions yet");
        check.Stdout.Should().Contain("FAIL  extensions|pg_stat_statements (a DBA-provided server's own: name it with --allow-extension)");
        var allowed = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin, "--allow-extension", "pg_stat_statements", "--allow-login", "it_repl",
            "--allow-setting", "work_mem");
        allowed.Stdout.Should().Contain("ok    extensions|plpgsql, timescaledb and timescaledb_toolkit, and allowed: pg_stat_statements")
            .And.Contain("ok    settings|no ALTER SYSTEM setting (postgresql.auto.conf) but the allowed: work_mem")
            .And.Contain("ok    role settings|none but the runtime login's statement timeout, and allowed: work_mem");
        Line(allowed, "FAIL  logins|").Should().NotContain("it_repl").And.Contain("it_bypass (bypassrls)");
        foreach (var (option, value) in new[] { ("--allow-extension", "pg-stat"), ("--allow-login", "It_Repl"), ("--allow-setting", "work-mem") })
        {
            var badName = await _target.OpsAsync(Ct, "check", option, value);
            badName.ExitCode.Should().NotBe(0);
            badName.Stderr.Should().Contain($"{option} must be");
        }
        // A superuser read that fails (here a superuser name the socket does not map) is a FAIL, never a pass.
        var unread = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin, "--superuser", "it_nobody");
        unread.Stdout.Should().Contain("FAIL  settings|postgresql.auto.conf could not be read as the superuser");

        // The review's escalation: as the migration login, a schema of its own with an aggregate and an operator under system
        // names that make a superuser of whoever runs them as one, a count(*) that always says 0, and that schema first on the
        // search path of every session of the database.
        await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), Query.HijackSearchPath);
        var hijacked = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        (await ScalarAsync<bool>(_target.ConnectionString("postgres", _target.SuperuserPassword), Query.MigrationLoginIsSuperuser)).Should().BeFalse(
            "no statement of check ran the planted objects as the superuser");
        hijacked.ExitCode.Should().NotBe(0);
        Line(hijacked, "FAIL  planted|").Should().Contain("function it_evil.string_agg").And.Contain("function it_evil.count").And.Contain("operator it_evil.~~")
            .And.Contain("database setting search_path=it_evil, pg_catalog, public");
        hijacked.Stdout.Should().Contain("FAIL  settings|ALTER SYSTEM settings not read: the superuser reads nothing while the line above fails");
        hijacked.Stdout.Should().Contain("FAIL  event triggers|it_event", "the report's own session keeps the catalog's search path, so count(*) is the system's");
        await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), Query.RevokePurgeFromPublic);
        FunctionsLine(await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin)).Should().Contain("public.it_definer")
            .And.NotContain("ariva_retention_purge", "the shipped body with the shipped grants");
        // ARV-189 (L2): the purge's other pinned properties, each forged as the migration login and put back: EXECUTE with grant
        // option, EXECUTE for another role, and another setting for its sessions.
        foreach (var (forge, back, why) in new[]
                 {
                     (Query.PurgeWithGrantOption, Query.PurgeWithoutGrantOption, "ariva_runtime may grant EXECUTE on"),
                     (Query.PurgeForReader, Query.PurgeNotForReader, "EXECUTE granted to another role"),
                     (Query.PurgeOtherTimeZone, Query.PurgeUtc, "another setting for its sessions")
                 })
        {
            await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), forge);
            FunctionsLine(await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin)).Should().Contain("public.ariva_retention_purge (plpgsql, SECURITY DEFINER)", why);
            await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), back);
            FunctionsLine(await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin)).Should().NotContain("ariva_retention_purge", $"{why}, put back");
        }

        await ExecuteAsync(_target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword), Query.ForgePurgeBody);
        FunctionsLine(await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin)).Should().Contain("public.ariva_retention_purge (plpgsql, SECURITY DEFINER)",
            "another body under the purge's name, its settings and grants unchanged");

        await ExecuteAsync(_target.ConnectionString("postgres", _target.SuperuserPassword), Query.GiveMigrationLoginReplication);
        var replication = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        replication.ExitCode.Should().NotBe(0);
        replication.Stderr.Should().Contain("neither superuser, replication nor bypassrls");
    }

    #endregion

    #region Helpers

    private Dictionary<string, string> HostSettings(ChartDatabase server, string runtimePassword) => new()
    {
        ["Database:Host"] = server.Hostname,
        ["Database:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Database:Name"] = ChartDatabase.Database,
        ["Database:Username"] = RuntimeLogin,
        ["Database:Password"] = runtimePassword,
        ["DataProtection:CertificatePath"] = _certificatePath,
        ["DataProtection:KeyPath"] = _keyPath
    };

    private static IConfiguration Configuration(Dictionary<string, string> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>Signs in with the password and the code of the current step plus <paramref name="stepOffset"/>; returns the access token.</summary>
    private static async Task<string> SignInAsync(HttpClient client, string seed, int stepOffset)
    {
        // Away from a step's end, so the server's step is the one the code was made for.
        while (30 - DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 30 < 4)
            await Task.Delay(500, Ct);
        var code = Totp.Code(Base32.Decode(seed), Totp.StepAt(DateTimeOffset.UtcNow) + stepOffset);
        using var response = await client.PostAsJsonAsync("api/auth/login", new LoginRequest(AdminUserName, AdminPassword, code), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "sign-in with the password and a code from the seed: " + body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("accessToken").GetString();
    }

    private static async Task<string> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}: {body}");
        return body;
    }

    private static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>The one line of an <c>ariva-db.sh check</c> report that starts with <paramref name="prefix"/>.</summary>
    private static string Line(ExecResult check, string prefix) =>
        check.Stdout.Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The functions line of an <c>ariva-db.sh check</c> report (ARV-111a), not ARV-183's other functions line.</summary>
    private static string FunctionsLine(ExecResult check) =>
        check.Stdout.Split('\n').Single(line => line.StartsWith("ok    functions|", StringComparison.Ordinal) || line.StartsWith("FAIL  functions|", StringComparison.Ordinal));

    private enum Query
    {
        One,
        IsSuperuser,
        CreateTable,
        RuntimeMemberships,
        AdminSeed,
        OutboundSecret,
        PublishedDemoSites,
        SeedTimeSeriesAndOutbox,
        QueueMinutes,
        SensingEvents,
        CompressedChunks,
        Hypertables,
        Jobs,
        Keys,
        Outbox,
        Unsent,
        UnsentSeqs,
        NewestCompacted,
        StreamOffsets,
        ForgetLatestScript,
        RememberLatestScript,
        PlantAsMigrationLogin,
        PlantAsSuperuser,
        AlterSystem,
        Subscription,
        HijackSearchPath,
        MigrationLoginIsSuperuser,
        GiveMigrationLoginReplication,
        RevokePurgeFromPublic,
        PurgeWithGrantOption,
        PurgeWithoutGrantOption,
        PurgeForReader,
        PurgeNotForReader,
        PurgeOtherTimeZone,
        PurgeUtc,
        ForgePurgeBody
    }

    // A switch over string literals keeps every command text constant (CA2100).
    private static string Sql(Query query) => query switch
    {
        Query.One => "SELECT 1",
        Query.IsSuperuser => "SELECT rolsuper FROM pg_roles WHERE rolname = current_user",
        Query.CreateTable => "CREATE TABLE it_not_allowed (id int)",
        Query.RuntimeMemberships => "SELECT string_agg(g.rolname, ',' ORDER BY g.rolname) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid WHERE m.member = current_user::regrole",
        Query.AdminSeed => "SELECT totp_secret_protected FROM \"user\" WHERE user_name = 'it.restore.admin'",
        Query.OutboundSecret => "SELECT secret_protected FROM outbound_endpoint WHERE code = 'restore-partner'",
        Query.PublishedDemoSites => "SELECT count(DISTINCT message_key) FROM outbox_message WHERE topic = 'ariva.topology.zone-profile-activated.v1'",
        Query.SeedTimeSeriesAndOutbox => """
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, entries, exits, waits, updated_on, queue_length)
            SELECT 'DMO/IT-RESTORE', t, 12, 3, 2, 1, now(), 4
              FROM generate_series(date_trunc('minute', now()) - interval '3 days', date_trunc('minute', now()) - interval '1 minute', interval '1 minute') t;
            INSERT INTO sensing_event (time_utc, site_code, queue_zone_name, kind, batch_id, ordinal, device_id, device_code, received_utc, flags, commissioned, track_id, x, y)
            SELECT t, 'DMO', 'IT-RESTORE', 'Track', gen_random_uuid(), 0, gen_random_uuid(), 'S-01', t, 0, true, 'trk', 1.0, 2.0
              FROM generate_series(now() - interval '4 days', now() - interval '2 days', interval '1 minute') t;
            SELECT count(compress_chunk(c, true)) FROM show_chunks('sensing_event', older_than => interval '1 day') c;
            INSERT INTO stream_offset (consumer_group, topic, partition_no, next_offset, updated_on)
            VALUES ('ariva-stream.queue-engine', 'ariva.device.track-sample.v1', 0, 4242, now());
            INSERT INTO outbox_message (id, topic, message_key, message_type, payload, created_on) VALUES
              (gen_random_uuid(), 'ariva.device.registry-changed.v1', 'it-device-1', 'DeviceRegistryChanged', '{"step": 1}', now() - interval '20 days'),
              (gen_random_uuid(), 'ariva.device.registry-changed.v1', 'it-device-1', 'DeviceRegistryChanged', '{"step": 2}', now() - interval '10 days'),
              (gen_random_uuid(), 'ariva.alert.state-changed.v1', 'it-alert-1', 'AlertStateChanged', '{"step": 1}', now() - interval '10 days');
            UPDATE outbox_message SET sent_on = greatest(created_on, now() - interval '1 day') WHERE sent_on IS NULL;
            """,
        Query.QueueMinutes => "SELECT count(*) FROM queue_minute",
        Query.SensingEvents => "SELECT count(*) FROM sensing_event",
        Query.CompressedChunks => "SELECT count(*) FROM timescaledb_information.chunks WHERE hypertable_name = 'sensing_event' AND is_compressed",
        Query.Hypertables => "SELECT count(*) FROM timescaledb_information.hypertables",
        Query.Jobs => "SELECT count(*) FROM timescaledb_information.jobs WHERE hypertable_name IS NOT NULL",
        Query.Keys => "SELECT count(*) FROM data_protection_key",
        Query.Outbox => "SELECT count(*) FROM outbox_message",
        Query.Unsent => "SELECT count(*) FROM outbox_message WHERE sent_on IS NULL",
        Query.UnsentSeqs => "SELECT string_agg(seq::text, ',' ORDER BY seq) FROM outbox_message WHERE sent_on IS NULL",
        Query.NewestCompacted => """
            SELECT string_agg(seq::text, ',' ORDER BY seq) FROM (
                SELECT DISTINCT ON (topic, message_key) seq FROM outbox_message
                 WHERE topic = ANY (@compacted)
                 ORDER BY topic, message_key, seq DESC) newest
            """,
        Query.StreamOffsets => "SELECT count(*) FROM stream_offset",
        Query.ForgetLatestScript => "DELETE FROM schema_version WHERE script_name = (SELECT max(script_name) FROM schema_version)",
        // The latest shipped script recorded again as the runner records it (its name and checksum are bound below).
        Query.RememberLatestScript => "INSERT INTO schema_version (script_name, checksum, execution_ms) VALUES (@name, @checksum, 0)",
        Query.PlantAsMigrationLogin => """
            CREATE FUNCTION public.it_definer() RETURNS int LANGUAGE sql SECURITY DEFINER AS 'SELECT 1';
            GRANT EXECUTE ON FUNCTION public.ariva_retention_purge(text, integer) TO PUBLIC;
            CREATE ROLE it_extra_member NOLOGIN;
            GRANT ariva_runtime TO it_extra_member;
            CREATE FUNCTION public.it_plain() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;
            CREATE RULE it_rule AS ON INSERT TO public.site DO ALSO NOTHING;
            CREATE OR REPLACE FUNCTION public.validation_row_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE PUBLICATION it_pub FOR TABLE public.site;
            GRANT UPDATE ON public.report_delivery TO ariva_runtime;
            DO $$ BEGIN
                EXECUTE (SELECT format('GRANT UPDATE ON %I.%I TO ariva_runtime', chunk_schema, chunk_name)
                           FROM timescaledb_information.chunks WHERE hypertable_name = 'sensing_event' ORDER BY chunk_name LIMIT 1);
                EXECUTE (SELECT format('GRANT UPDATE ON %I.%I TO ariva_runtime', chunk_schema, chunk_name || '_compressed')
                           FROM timescaledb_information.chunks WHERE hypertable_name = 'sensing_event' AND is_compressed ORDER BY chunk_name LIMIT 1);
                EXECUTE (SELECT format('GRANT INSERT ON %I.%I TO ariva_runtime', materialization_hypertable_schema, materialization_hypertable_name)
                           FROM timescaledb_information.continuous_aggregates WHERE view_name = 'queue_minute_15m');
            END $$;
            GRANT MAINTAIN ON public.sensing_event TO ariva_runtime;
            CREATE VIEW public.it_deliveries AS SELECT * FROM public.report_delivery;
            CREATE SCHEMA it_side;
            GRANT CREATE ON SCHEMA it_side TO ariva_app;
            CREATE VIEW it_side.it_view AS SELECT 1 AS one;
            GRANT SELECT ON it_side.it_view TO ariva_runtime;
            CREATE TABLE it_side.it_child () INHERITS (public.alert_rule);
            GRANT UPDATE ON SEQUENCE public.outbox_message_seq_seq TO ariva_runtime;
            GRANT SELECT ON public.integration_call TO ariva_app;
            GRANT SELECT ON public.audit_entry TO ariva_runtime WITH GRANT OPTION;
            GRANT SELECT ON public.site TO PUBLIC;
            GRANT CREATE ON DATABASE ariva TO ariva_runtime;
            ALTER DEFAULT PRIVILEGES GRANT TRIGGER ON TABLES TO ariva_runtime;
            ALTER DEFAULT PRIVILEGES IN SCHEMA _timescaledb_internal GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ariva_runtime;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT TRUNCATE ON TABLES TO ariva_runtime;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT UPDATE ON SEQUENCES TO ariva_runtime WITH GRANT OPTION;
            """,
        Query.PlantAsSuperuser => """
            CREATE FUNCTION public.it_c_function() RETURNS language_handler AS '$libdir/plpgsql', 'plpgsql_call_handler' LANGUAGE c;
            CREATE FUNCTION public.it_event_function() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$;
            CREATE EVENT TRIGGER it_event ON ddl_command_start EXECUTE FUNCTION public.it_event_function();
            CREATE ROLE it_privileged LOGIN CREATEROLE;
            CREATE FUNCTION pg_catalog.it_shadow() RETURNS int LANGUAGE sql AS 'SELECT 1';
            CREATE EXTENSION pg_stat_statements;
            CREATE ROLE it_super NOLOGIN SUPERUSER;
            CREATE ROLE it_back LOGIN IN ROLE it_super;
            CREATE ROLE it_repl LOGIN REPLICATION;
            CREATE ROLE it_bypass LOGIN BYPASSRLS;
            GRANT pg_execute_server_program TO ariva_runtime;
            ALTER ROLE it_back SET work_mem = '16MB';
            CREATE FUNCTION public.zone_profile_immutable(integer) RETURNS int LANGUAGE sql AS 'SELECT 1';
            CREATE FUNCTION pg_catalog.it_hidden() RETURNS int LANGUAGE sql AS 'SELECT 1';
            ALTER EXTENSION plpgsql ADD FUNCTION pg_catalog.it_hidden();
            CREATE OR REPLACE FUNCTION pg_catalog.reverse(text) RETURNS text LANGUAGE sql IMMUTABLE STRICT AS $$ SELECT $1 $$;
            GRANT UPDATE ON _timescaledb_catalog.hypertable TO PUBLIC;
            GRANT UPDATE (config) ON _timescaledb_catalog.bgw_job TO ariva_runtime;
            CREATE VIEW information_schema.it_view AS SELECT * FROM public.retention_period;
            ALTER VIEW information_schema.it_view OWNER TO ariva;
            GRANT UPDATE ON information_schema.it_view TO ariva_runtime;
            DO $$
            DECLARE target text := (SELECT format('%I.%I', chunk_schema, chunk_name || '_compressed') FROM timescaledb_information.chunks
                                     WHERE hypertable_name = 'queue_minute' AND NOT is_compressed ORDER BY chunk_name LIMIT 1);
            BEGIN
                EXECUTE format('CREATE VIEW %s AS SELECT * FROM public.retention_period', target);
                EXECUTE format('ALTER VIEW %s OWNER TO ariva', target);
                EXECUTE format('GRANT UPDATE ON %s TO ariva_runtime', target);
            END $$;
            CREATE VIEW _timescaledb_internal._direct_view_999 AS SELECT * FROM public.queue_minute_shadow;
            ALTER VIEW _timescaledb_internal._direct_view_999 OWNER TO ariva;
            GRANT SELECT ON _timescaledb_internal._direct_view_999 TO ariva_runtime;
            UPDATE _timescaledb_catalog.continuous_agg SET direct_view_name = '_direct_view_999' WHERE user_view_name = 'line_minute_15m';
            DO $$ BEGIN
                EXECUTE (SELECT format('CREATE VIEW _timescaledb_internal._direct_view_998 AS SELECT chunk_id, chunk_seq FROM %s', reltoastrelid::regclass)
                           FROM pg_class WHERE oid = 'public.alert_rule'::regclass);
            END $$;
            ALTER VIEW _timescaledb_internal._direct_view_998 OWNER TO ariva;
            GRANT SELECT ON _timescaledb_internal._direct_view_998 TO ariva_runtime;
            GRANT SELECT ON pg_catalog.pg_statistic TO PUBLIC;
            GRANT SELECT ON pg_catalog.pg_authid TO ariva_runtime;
            GRANT SELECT ON information_schema._pg_user_mappings TO PUBLIC;
            """,
        Query.AlterSystem => "ALTER SYSTEM SET work_mem = '8MB'",
        Query.Subscription => "CREATE SUBSCRIPTION it_sub CONNECTION 'host=127.0.0.1 dbname=ariva' PUBLICATION it_pub WITH (connect = false)",
        Query.HijackSearchPath => """
            CREATE SCHEMA it_evil;
            CREATE FUNCTION it_evil.it_escalate() RETURNS boolean LANGUAGE plpgsql AS $$
            BEGIN
                IF (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user) THEN
                    EXECUTE 'ALTER ROLE ariva SUPERUSER';
                END IF;
                RETURN true;
            END $$;
            CREATE FUNCTION it_evil.it_like(text, text) RETURNS boolean LANGUAGE sql AS 'SELECT it_evil.it_escalate()';
            CREATE OPERATOR it_evil.~~ (LEFTARG = text, RIGHTARG = text, FUNCTION = it_evil.it_like);
            CREATE FUNCTION it_evil.it_step(text, text, text) RETURNS text LANGUAGE sql AS 'SELECT CASE WHEN it_evil.it_escalate() THEN $2 END';
            CREATE AGGREGATE it_evil.string_agg(text, text) (SFUNC = it_evil.it_step, STYPE = text);
            CREATE FUNCTION it_evil.it_zero(bigint) RETURNS bigint LANGUAGE sql AS 'SELECT 0::bigint';
            CREATE AGGREGATE it_evil.count(*) (SFUNC = it_evil.it_zero, STYPE = bigint, INITCOND = '0');
            ALTER DATABASE ariva SET search_path = it_evil, pg_catalog, public;
            """,
        Query.MigrationLoginIsSuperuser => "SELECT rolsuper FROM pg_roles WHERE rolname = 'ariva'",
        Query.GiveMigrationLoginReplication => "ALTER ROLE ariva REPLICATION",
        Query.RevokePurgeFromPublic => "REVOKE EXECUTE ON FUNCTION public.ariva_retention_purge(text, integer) FROM PUBLIC",
        Query.PurgeWithGrantOption => "GRANT EXECUTE ON FUNCTION public.ariva_retention_purge(text, integer) TO ariva_runtime WITH GRANT OPTION",
        Query.PurgeWithoutGrantOption => "REVOKE GRANT OPTION FOR EXECUTE ON FUNCTION public.ariva_retention_purge(text, integer) FROM ariva_runtime",
        Query.PurgeForReader => "GRANT EXECUTE ON FUNCTION public.ariva_retention_purge(text, integer) TO ariva_validation_reader",
        Query.PurgeNotForReader => "REVOKE EXECUTE ON FUNCTION public.ariva_retention_purge(text, integer) FROM ariva_validation_reader",
        // ALTER FUNCTION ... SET replaces the setting where it stands, so setting it back restores the shipped array.
        Query.PurgeOtherTimeZone => "ALTER FUNCTION public.ariva_retention_purge(text, integer) SET TimeZone = 'Asia/Dubai'",
        Query.PurgeUtc => "ALTER FUNCTION public.ariva_retention_purge(text, integer) SET TimeZone = 'UTC'",
        // CREATE OR REPLACE keeps the function's grants; the settings are the shipped ones, so only the body differs.
        Query.ForgePurgeBody => """
            CREATE OR REPLACE FUNCTION public.ariva_retention_purge(p_data_set text, p_limit integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp SET TimeZone = 'UTC' AS $$ BEGIN RETURN 0; END $$
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(query))
    };

    private static async Task<T> ScalarAsync<T>(string connectionString, Query query)
    {
        var sql = Sql(query);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Query) above
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        if (query == Query.NewestCompacted)
            command.Parameters.AddWithValue("compacted", TopicCatalog.CompactedTopics.ToArray());
        return (T)await command.ExecuteScalarAsync(Ct);
    }

    private static async Task ExecuteAsync(string connectionString, Query query)
    {
        var sql = Sql(query);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Query) above
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        if (query == Query.RememberLatestScript)
        {
            var latest = SqlScriptCatalog.Embedded()[^1];
            command.Parameters.AddWithValue("name", latest.Name);
            command.Parameters.AddWithValue("checksum", latest.Checksum);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    #endregion
}
