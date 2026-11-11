using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ariva.Api.Common.HealthChecks;
using Ariva.Di;
using Ariva.Infra.DataProtection;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// ARV-244 (CWE-345, CWE-269): TimescaleDB restores a logical dump only into the extension version the dump was taken at (its
/// catalog travels as data), so a backup taken before an image bump is restored through its own version. Four servers are
/// started as the TimescaleDB chart starts one (<see cref="ChartDatabase"/>). The source is a database created under the
/// previous image: both extensions at the newest older versions the image ships, the release's migration job on them, time
/// series with a compressed chunk; <c>ariva-db.sh backup</c> takes its dump, and the manifest records the old version.
/// <para>
/// Into the fresh target, each of these is refused before anything changes: a dump recording a TimescaleDB the image does not
/// ship (one newer than the image, and one the image lists only as the end of a downgrade script, without its library), a dump
/// recording no version, one whose metadata rows pass their 64 KiB bound, the older dump with another version given, and a
/// malformed version. <c>check-dump</c> reports the dump's version, from a file and from stdin; from stdin (the stream
/// ariva-ops.mjs sends) it also compares a version given, refuses the oversized dump, and leaves <c>/tmp</c> as it found it.
/// The older dump is then restored from stdin as ariva-ops.mjs restores it: the extension created at the dump's version in the
/// empty database, the dump restored, its jobs judged, check's lines passed before the update, both extensions updated to the
/// image's versions and the restore's own check passing; every row, hypertable and job is kept. After the release's migration
/// job, check passes and Ariva.Api.Main and Ariva.Api.Integration start on the restored database (their schema check included).
/// </para>
/// <para>
/// Its review: a dump that brings a function under a system name is restored at its own version and stopped by check's lines
/// before the update runs as the superuser on it; objects of no extension in the empty database (a function, an enum and a
/// composite type, a schema) and a setting for every session of it refuse a restore that would replace the extension before
/// anything changes, and (its second round) so does a session of the migration login, before the extension is dropped; and a
/// stream restored with another version than its own keeps the database in TimescaleDB's restore mode, with no update.
/// <see cref="BackupRestoreTests"/> restores a dump of the image's own version.
/// </para>
/// </summary>
public sealed partial class TimescaleVersionRestoreTests : IAsyncLifetime
{
    private const string RuntimeLogin = ChartDatabase.MainLogin;
    private const string Superuser = "postgres";
    private const string DumpPath = ChartDatabase.BackupDirectory + "/ariva-older-timescale.dump";
    private const string NewerDumpPath = ChartDatabase.BackupDirectory + "/ariva-newer-timescale.dump";
    private const string NoLibraryDumpPath = ChartDatabase.BackupDirectory + "/ariva-no-library-timescale.dump";
    private const string NoVersionDumpPath = ChartDatabase.BackupDirectory + "/ariva-no-version-timescale.dump";
    private const string OversizedDumpPath = ChartDatabase.BackupDirectory + "/ariva-oversized-metadata.dump";
    private const string PlantedDumpPath = ChartDatabase.BackupDirectory + "/ariva-planted-timescale.dump";

    // A TimescaleDB newer than any image of this release: a dump of it needs a newer image, never this one.
    private const string NewerVersion = "99.0.0";

    private static readonly string SourceRuntimePassword = "rt-" + Hex(12);
    private static readonly string RestoredRuntimePassword = "rt2-" + Hex(12);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-timescale-restore-" + Guid.NewGuid().ToString("N"));
    private string _certificatePath;
    private string _keyPath;
    private string _signingCertificatePath;
    private string _signingKeyPath;
    private ChartDatabase _source;
    private ChartDatabase _target;
    private ChartDatabase _planted;
    private ChartDatabase _stuck;

    public async ValueTask InitializeAsync()
    {
        // The folder every server sees as /backup: the database user (uid 1000) writes the dumps there.
        var backup = Path.Combine(_workDirectory, "backup");
        Directory.CreateDirectory(backup);
        File.SetUnixFileMode(backup, (UnixFileMode)0b111_111_111);

        // The ariva-dataprotection secret as the chart mounts it, for the hosts started on the restored database.
        var secrets = Path.Combine(_workDirectory, "dataprotection");
        Directory.CreateDirectory(secrets);
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        var request = new CertificateRequest("CN=Ariva Data Protection (TimescaleDB version restore test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _certificatePath = Path.Combine(secrets, "tls.crt");
        _keyPath = Path.Combine(secrets, "tls.key");
        await File.WriteAllTextAsync(_certificatePath, certificate.ExportCertificatePem(), Ct);
        await File.WriteAllTextAsync(_keyPath, rsa.ExportPkcs8PrivateKeyPem(), Ct);
        File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // ARV-211: the ariva-dataprotection-signing secret as the chart mounts it: the key ring's own ECDSA P-256 signing certificate.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signingRequest = new CertificateRequest("CN=Ariva key ring signing (TimescaleDB version restore test)", ecdsa, HashAlgorithmName.SHA256);
        signingRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var signing = signingRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _signingCertificatePath = Path.Combine(secrets, "signing.crt");
        _signingKeyPath = Path.Combine(secrets, "signing.key");
        await File.WriteAllTextAsync(_signingCertificatePath, signing.ExportCertificatePem(), Ct);
        await File.WriteAllTextAsync(_signingKeyPath, ecdsa.ExportPkcs8PrivateKeyPem(), Ct);
        File.SetUnixFileMode(_signingKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var servers = await Task.WhenAll(ChartDatabase.StartAsync(backup, Ct), ChartDatabase.StartAsync(backup, Ct),
            ChartDatabase.StartAsync(backup, Ct), ChartDatabase.StartAsync(backup, Ct));
        (_source, _target, _planted, _stuck) = (servers[0], servers[1], servers[2], servers[3]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in new[] { _source, _target, _planted, _stuck })
        {
            if (server is not null)
                await server.DisposeAsync();
        }
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    [Fact(Timeout = 900_000)]
    public async Task Restore_Should_GoThroughTheDumpsTimescaleDbVersion_When_TheBackupWasTakenUnderThePreviousVersionTheImageShips()
    {
        // Arrange: the source as the previous image left it (both extensions at the newest older version the image ships and
        // updates from), the release's migration job on them, and time series with a compressed chunk.
        var shipped = Versions(await ScalarAsync(SuperuserConnection(_target), Query.ShippedVersions));
        var timescale = await PreviousVersionAsync(Query.PreviousTimescale);
        var toolkit = await PreviousVersionAsync(Query.PreviousToolkit);
        await SuperuserAsync(_source, "DROP EXTENSION timescaledb_toolkit");
        await SuperuserAsync(_source, "DROP EXTENSION timescaledb");
        await SuperuserAsync(_source, $"CREATE EXTENSION timescaledb VERSION '{timescale}'");
        await SuperuserAsync(_source, $"CREATE EXTENSION timescaledb_toolkit VERSION '{toolkit}'");
        (await DatabaseMigration.RunAsync(Configuration(_source.MigrationSettings(RuntimeLogin, SourceRuntimePassword)), Ct)).Should().Be(0);
        await ExecuteAsync(MigrationConnection(_source), Query.SeedTimeSeries);
        var before = await ScalarAsync(MigrationConnection(_source), Query.TimescaleState);
        before.Should().MatchRegex("^[1-9][0-9]* hypertables, [1-9][0-9]* compressed sensing chunks, [1-9][0-9]* sensing events, [1-9][0-9]* queue minutes, jobs ",
            "the dump carries TimescaleDB catalog rows: hypertables, chunks, compression and jobs");
        // The migration job's pooled connections are the job's own; in a cluster its pod has ended by now.
        NpgsqlConnection.ClearAllPools();

        var manifest = await _source.OpsAsync(Ct, "manifest");
        manifest.ExitCode.Should().Be(0, manifest.Stderr);
        using (var json = JsonDocument.Parse(manifest.Stdout))
            json.RootElement.GetProperty("extensions").GetProperty("timescaledb").GetString().Should().Be(timescale, "the manifest records the backup's TimescaleDB version");
        await BackupAsync(DumpPath);
        await BackupsTheDumpsToRefuseAsync(timescale);

        // Act and Assert: what is refused before anything changes, and check-dump from a file and from stdin.
        await RefusesWithNothingChangedAsync(shipped["timescaledb"], timescale);
        await ChecksTheStreamAsync(shipped["timescaledb"], timescale);

        // Act: the older dump restored from stdin, as ariva-ops.mjs restores it (after its check pass reported the version).
        var restore = await StreamAsync(_target, StreamCase.Restore, timescale);

        // Assert: created at the dump's version, restored, jobs judged, judged again before the update, updated to the image's,
        // and its own check passes.
        restore.ExitCode.Should().Be(0, restore.Stderr + restore.Stdout);
        restore.Stderr.Should().Contain($"the extension is created at {timescale} first and updated to {shipped["timescaledb"]} afterwards")
            .And.Contain($"dropping the image's TimescaleDB {shipped["timescaledb"]} from the empty {ChartDatabase.Database}")
            .And.Contain($"creating TimescaleDB {timescale} in {ChartDatabase.Database}")
            .And.Contain("TimescaleDB's jobs checked before its scheduler starts")
            .And.Contain("the restored database passes the check before the extensions' update (extension versions and members aside)")
            .And.Contain($"updating timescaledb from {timescale} to {shipped["timescaledb"]}")
            .And.NotContain("updating timescaledb_toolkit", "the toolkit stays at the image's version: a dump carries none of its rows")
            .And.Contain("restore finished. Next: run the migration job");
        restore.Stdout.Should().Contain("ok    extension versions|TimescaleDB's extensions at the versions the image ships")
            .And.Contain($"ok    extension members|timescaledb {shipped["timescaledb"]} and timescaledb_toolkit {shipped["timescaledb_toolkit"]}")
            .And.Contain("ok    schema|Ariva's ").And.Contain("ok    planted|")
            .And.NotContain("FAIL", "the check before the update prints only what fails, and nothing did");
        Versions(await ScalarAsync(SuperuserConnection(_target), Query.InstalledVersions)).Should().Equal(shipped);
        (await ScalarAsync(SuperuserConnection(_target), Query.ExtensionPlaces)).Should().Be("timescaledb public postgres, timescaledb_toolkit public postgres",
            "the extension is created in public, owned by the bootstrap superuser, as the image creates it");
        (await ScalarAsync(MigrationConnection(_target), Query.RecordedVersion)).Should().Be(shipped["timescaledb"]);
        (await ScalarAsync(MigrationConnection(_target), Query.TimescaleState)).Should().Be(before, "every hypertable, compressed chunk, row and job is kept");

        // The release's migration job, then check, then the hosts on the restored database.
        (await DatabaseMigration.RunAsync(Configuration(_target.MigrationSettings(RuntimeLogin, RestoredRuntimePassword)), Ct)).Should().Be(0);
        NpgsqlConnection.ClearAllPools();
        var check = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        check.ExitCode.Should().Be(0, check.Stdout + check.Stderr);
        check.Stdout.Should().Contain($"ok    runtime login|{RuntimeLogin} holds ariva_main and ariva_runtime (DML only)", "ARV-083a: Ariva.Api.Main's own login")
            .And.Contain("ok    extension versions|").And.NotContain("FAIL", check.Stdout);

        await using (var main = new DeployedHost<Ariva.Api.Main._IAssemblyMark>(HostSettings(_target, RestoredRuntimePassword)))
            (await StartupProbeAsync(main.Client())).Should().Be(HttpStatusCode.OK, "Ariva.Api.Main starts on the restored database, its schema check included");
        // ARV-083a: Ariva.Api.Integration on its own login, as the chart starts it.
        var (integrationLogin, integrationPassword) = _target.HostLogin("Integration");
        await using (var integration = new DeployedHost<Ariva.Api.Integration._IAssemblyMark>(HostSettings(_target, integrationPassword, integrationLogin)))
            (await StartupProbeAsync(integration.Client())).Should().Be(HttpStatusCode.OK, "Ariva.Api.Integration starts on the restored database, its schema check included");

        // Its review: what the dump brings is judged before the update runs on it, and the empty database before the extension
        // is replaced; a stream restored with another version than its own stays in restore mode.
        await StopsBeforeTheUpdateAsync(timescale);
        await RefusesCollisionsThenKeepsRestoreModeAsync(shipped["timescaledb"], timescale);
    }

    #region Cases

    /// <summary>
    /// The dumps the restore refuses, each a backup of the source with one thing changed for that backup only (as the superuser
    /// or the migration login, then put back): a TimescaleDB the image does not ship (one newer than the image, and one it lists
    /// without its library), no timescaledb_version row, metadata rows past their 64 KiB bound, and a function under a system
    /// name that the source's migration login created.
    /// </summary>
    private async Task BackupsTheDumpsToRefuseAsync(string timescale)
    {
        var noLibrary = await ListedWithoutLibraryAsync();
        await BackupRecordingAsync(NewerVersion, NewerDumpPath, timescale);
        await BackupRecordingAsync(noLibrary, NoLibraryDumpPath, timescale);

        await ExecuteAsync(SuperuserConnection(_source), Query.HideVersion);
        await BackupAsync(NoVersionDumpPath);
        await ExecuteAsync(SuperuserConnection(_source), Query.ShowVersion);

        await ExecuteAsync(SuperuserConnection(_source), Query.PadMetadata);
        await BackupAsync(OversizedDumpPath);
        await ExecuteAsync(SuperuserConnection(_source), Query.UnpadMetadata);

        await ExecuteAsync(MigrationConnection(_source), Query.PlantLower);
        await BackupAsync(PlantedDumpPath);
        await ExecuteAsync(MigrationConnection(_source), Query.UnplantLower);
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Each refused into the fresh target with nothing dropped, created or restored.</summary>
    private async Task RefusesWithNothingChangedAsync(string imageVersion, string timescale)
    {
        var fresh = await ScalarAsync(SuperuserConnection(_target), Query.TargetState);
        var noLibrary = await ListedWithoutLibraryAsync();
        foreach (var (dump, version) in new[] { (NewerDumpPath, NewerVersion), (NoLibraryDumpPath, noLibrary) })
        {
            var refused = await _target.OpsAsync(Ct, "restore", "--input", dump);
            refused.ExitCode.Should().NotBe(0, refused.Stdout);
            refused.Stderr.Should().Contain($"the dump is of TimescaleDB {version}, which this image does not ship").And.Contain("nothing was changed")
                .And.NotContain("creating Ariva's group roles").And.NotContain("dropping the image's TimescaleDB");
            (await ScalarAsync(SuperuserConnection(_target), Query.TargetState)).Should().Be(fresh, $"a restore of a dump of TimescaleDB {version} changes nothing");
        }

        var noVersion = await _target.OpsAsync(Ct, "restore", "--input", NoVersionDumpPath);
        noVersion.ExitCode.Should().NotBe(0, noVersion.Stdout);
        noVersion.Stderr.Should().Contain("the dump records no TimescaleDB version (no single timescaledb_version row in _timescaledb_catalog.metadata)")
            .And.Contain("nothing was restored");
        var oversized = await _target.OpsAsync(Ct, "restore", "--input", OversizedDumpPath);
        oversized.ExitCode.Should().NotBe(0, oversized.Stdout);
        oversized.Stderr.Should().Contain("the dump's TimescaleDB catalog metadata (_timescaledb_catalog.metadata) is larger than 64 KiB");
        var mismatch = await _target.OpsAsync(Ct, "restore", "--input", DumpPath, "--timescaledb-version", imageVersion);
        mismatch.ExitCode.Should().NotBe(0, mismatch.Stdout);
        mismatch.Stderr.Should().Contain($"the dump records TimescaleDB {timescale}, not the {imageVersion} given").And.Contain("nothing was restored");
        var malformed = await _target.OpsAsync(Ct, "restore", "--input", DumpPath, "--timescaledb-version", timescale + "';--");
        malformed.ExitCode.Should().NotBe(0, malformed.Stdout);
        malformed.Stderr.Should().Contain("--timescaledb-version must be a plain dotted number");
        (await ScalarAsync(SuperuserConnection(_target), Query.TargetState)).Should().Be(fresh, "a refused restore changes nothing");
    }

    /// <summary>
    /// check-dump reports the dump's version from a file and from stdin; from stdin, the one pass ariva-ops.mjs's check pass
    /// makes, it compares a version given and bounds the metadata it reads, and its private folder leaves no trace in /tmp.
    /// </summary>
    private async Task ChecksTheStreamAsync(string imageVersion, string timescale)
    {
        var fromFile = await _target.OpsAsync(Ct, "check-dump", "--input", DumpPath);
        fromFile.ExitCode.Should().Be(0, fromFile.Stderr);
        fromFile.Stdout.Should().Be($"timescaledb {timescale}\n", "check-dump reports the version the dump records, which ariva-ops.mjs passes to the restore");

        var tmp = await TmpAsync(_target);
        var fromStream = await StreamAsync(_target, StreamCase.CheckDump);
        fromStream.ExitCode.Should().Be(0, fromStream.Stderr);
        fromStream.Stdout.Should().Be($"timescaledb {timescale}\n", "from stdin the report is the same one line");
        fromStream.Stderr.Should().Contain("dump checked:").And.Contain($"the dump records TimescaleDB {timescale}");
        (await TmpAsync(_target)).Should().Be(tmp, "the stream's private folder and FIFOs are removed");

        var given = await StreamAsync(_target, StreamCase.CheckDumpGiven, imageVersion);
        given.ExitCode.Should().NotBe(0, given.Stdout);
        given.Stdout.Should().BeEmpty("nothing is reported for a dump that disagrees with the version given");
        given.Stderr.Should().Contain($"the dump records TimescaleDB {timescale}, not the {imageVersion} given (the signed manifest's): refused");

        var oversized = await StreamAsync(_target, StreamCase.CheckDumpOversized);
        oversized.ExitCode.Should().NotBe(0, oversized.Stdout);
        oversized.Stderr.Should().Contain("the dump's TimescaleDB catalog metadata (_timescaledb_catalog.metadata) is larger than 64 KiB");
        (await TmpAsync(_target)).Should().Be(tmp, "a refused stream leaves no trace in /tmp either");
    }

    /// <summary>
    /// A dump that brings a function under a system name (public.lower) is restored at its own version, and check's lines
    /// stop the restore before the extensions' update would run TimescaleDB's update scripts as the superuser on it.
    /// </summary>
    private async Task StopsBeforeTheUpdateAsync(string timescale)
    {
        var planted = await _planted.OpsAsync(Ct, "restore", "--input", PlantedDumpPath);

        planted.ExitCode.Should().NotBe(0, planted.Stdout);
        planted.Stderr.Should().Contain("checking the restored database before the extensions' update")
            .And.Contain("the restored database fails the check above before the extensions' update").And.Contain("nothing was updated")
            .And.NotContain("updating timescaledb from");
        planted.Stdout.Should().Contain("FAIL  planted|function public.lower").And.NotContain("FAIL  extension versions")
            .And.NotContain("FAIL  extension members").And.NotContain("ok    ");
        (await ScalarAsync(SuperuserConnection(_planted), Query.InstalledTimescale)).Should().Be(timescale, "the update did not run");
        (await ScalarAsync(SuperuserConnection(_planted), Query.Restoring)).Should().Be("off", "the jobs were judged before restore mode ended");
    }

    /// <summary>
    /// A session of the migration login refuses a restore that would replace the image's extension before the extension is
    /// dropped. Objects of no extension in the empty database, and a setting for every session of it, refuse such a restore
    /// before anything changes. A restore of the image's own version replaces nothing, so they do
    /// not stop it; given the image's version for the older dump on stdin, its catalog check after pg_restore keeps the database
    /// in TimescaleDB's restore mode and nothing is updated.
    /// </summary>
    private async Task RefusesCollisionsThenKeepsRestoreModeAsync(string imageVersion, string timescale)
    {
        // Round 2 of the review: a session of the migration login (held open here) could create, between the DROP and the
        // CREATE, an object the CREATE collides with, so the restore refuses it before the image's extension is dropped.
        await using (var session = new NpgsqlConnection(MigrationConnection(_stuck)))
        {
            await session.OpenAsync(Ct);
            var busy = await _stuck.OpsAsync(Ct, "restore", "--input", DumpPath);
            busy.ExitCode.Should().NotBe(0, busy.Stdout);
            busy.Stderr.Should().Contain($"Ariva's logins still have sessions on {ChartDatabase.Database}: {ChartDatabase.MigrationLogin} (1)")
                .And.Contain("nothing was dropped or restored").And.NotContain("dropping the image's TimescaleDB");
        }
        (await ScalarAsync(SuperuserConnection(_stuck), Query.InstalledTimescale)).Should().Be(imageVersion, "the image's extension was not dropped");

        await ExecuteAsync(MigrationConnection(_stuck), Query.PlantCollisions);
        NpgsqlConnection.ClearAllPools();
        var planted = await ScalarAsync(SuperuserConnection(_stuck), Query.TargetState);

        var collision = await _stuck.OpsAsync(Ct, "restore", "--input", DumpPath);

        collision.ExitCode.Should().NotBe(0, collision.Stdout);
        collision.Stderr.Should().Contain("refused before anything changes: ").And.Contain("database setting work_mem=8MB")
            .And.Contain("function public.arv244_probe").And.Contain("schema arv244_extra").And.Contain("type public.arv244_kind")
            .And.Contain("type public.arv244_pair").And.NotContain("creating Ariva's group roles").And.NotContain("dropping the image's TimescaleDB");
        (await ScalarAsync(SuperuserConnection(_stuck), Query.TargetState)).Should().Be(planted, "a refused restore changes nothing");

        var wrong = await StreamAsync(_stuck, StreamCase.Restore, imageVersion);

        wrong.ExitCode.Should().NotBe(0, wrong.Stdout);
        wrong.Stderr.Should().Contain($"restoring into {ChartDatabase.Database} (TimescaleDB {imageVersion}, the version given for the dump)")
            .And.Contain($"the restored dump records TimescaleDB {timescale}, not {imageVersion}, the version it was restored into")
            .And.Contain("the database stays in TimescaleDB's restore mode").And.NotContain("updating timescaledb")
            .And.NotContain("checking the restored database before the extensions' update");
        (await ScalarAsync(SuperuserConnection(_stuck), Query.Restoring)).Should().Be("on", "no job of the dump runs before it is judged");
        (await ScalarAsync(SuperuserConnection(_stuck), Query.InstalledTimescale)).Should().Be(imageVersion);
    }

    #endregion

    #region Helpers

    /// <summary>The newest older version of an extension that the image ships an install script and an update path for.</summary>
    private async Task<string> PreviousVersionAsync(Query query)
    {
        var version = await ScalarAsync(SuperuserConnection(_target), query);
        DottedVersion().IsMatch(version ?? string.Empty).Should().BeTrue($"the image ships an older version it updates from ({query})");
        return version;
    }

    /// <summary>
    /// A TimescaleDB version the image lists among its available versions without the version's own library: in
    /// pg17-ts2.31, 2.16.1, which only a downgrade script from 2.17.0 reaches. Creating it fails once the session has loaded
    /// another library, so a restore must refuse it before it drops the image's extension.
    /// </summary>
    private async Task<string> ListedWithoutLibraryAsync()
    {
        var listed = (await ScalarAsync(SuperuserConnection(_target), Query.ListedTimescaleVersions)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var libraryDirectory = await _target.ExecAsync(Ct, "pg_config", "--pkglibdir");
        libraryDirectory.ExitCode.Should().Be(0, libraryDirectory.Stderr);
        var files = await _target.ExecAsync(Ct, "ls", "-1", libraryDirectory.Stdout.Trim());
        files.ExitCode.Should().Be(0, files.Stderr);
        var libraries = files.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var withoutLibrary = listed.Where(v => DottedVersion().IsMatch(v) && !libraries.Contains($"timescaledb-{v}.so")).ToList();
        withoutLibrary.Should().NotBeEmpty("the pinned image lists an older TimescaleDB it has no library for (pg17-ts2.31: 2.16.1)");
        return withoutLibrary[0];
    }

    private async Task BackupAsync(string path)
    {
        var backup = await _source.OpsAsync(Ct, "backup", "--output", path);
        backup.ExitCode.Should().Be(0, backup.Stderr);
    }

    /// <summary>
    /// A backup of the source whose TimescaleDB catalog records <paramref name="version"/> (set as the superuser for this one
    /// backup, then put back to <paramref name="actual"/>), as a dump taken under an image of that version records it.
    /// </summary>
    private async Task BackupRecordingAsync(string version, string path, string actual)
    {
        await ExecuteAsync(SuperuserConnection(_source), Query.RecordVersion, version);
        var backup = await _source.OpsAsync(Ct, "backup", "--output", path);
        await ExecuteAsync(SuperuserConnection(_source), Query.RecordVersion, actual);
        backup.ExitCode.Should().Be(0, backup.Stderr);
    }

    private Dictionary<string, string> HostSettings(ChartDatabase server, string runtimePassword, string login = RuntimeLogin) => new()
    {
        ["Database:Host"] = server.Hostname,
        ["Database:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Database:Name"] = ChartDatabase.Database,
        ["Database:Username"] = login,
        ["Database:Password"] = runtimePassword,
        ["DataProtection:CertificatePath"] = _certificatePath,
        ["DataProtection:KeyPath"] = _keyPath,
        ["DataProtection:Signing:CertificatePath"] = _signingCertificatePath,
        ["DataProtection:Signing:KeyPath"] = _signingKeyPath
    };

    private static async Task<HttpStatusCode> StartupProbeAsync(HttpClient client)
    {
        using var response = await client.GetAsync(HealthEndpoints.StartupPath, Ct);
        return response.StatusCode;
    }

    private static IConfiguration Configuration(Dictionary<string, string> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static string MigrationConnection(ChartDatabase server) => server.ConnectionString(ChartDatabase.MigrationLogin, server.MigrationPassword);

    private static string SuperuserConnection(ChartDatabase server) => server.ConnectionString(Superuser, server.SuperuserPassword);

    private static Dictionary<string, string> Versions(string rows) => rows.Split(';').Select(r => r.Split('=')).ToDictionary(p => p[0], p => p[1]);

    private static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>What the server's /tmp holds, one name a line: the pod's /tmp, where the script's private folder goes.</summary>
    private static async Task<string> TmpAsync(ChartDatabase server)
    {
        var listing = await server.ExecAsync(Ct, "ls", "-A", "/tmp");
        listing.ExitCode.Should().Be(0, listing.Stderr);
        return listing.Stdout;
    }

    /// <summary>
    /// A statement as the database superuser over the server's socket, in a psql session of its own: a session that has
    /// loaded one version of TimescaleDB's library cannot create another (the same helper as ExtensionUpdateTests').
    /// </summary>
    private static async Task SuperuserAsync(ChartDatabase server, string sql)
    {
        var result = await server.ExecAsync(Ct, "psql", "--no-psqlrc", "--quiet", "--set=ON_ERROR_STOP=1", "--host=/var/run/postgresql",
            "--username=" + Superuser, "--dbname=" + ChartDatabase.Database, "--command=" + sql);
        result.ExitCode.Should().Be(0, $"{sql}: {result.Stderr}");
    }

    /// <summary>
    /// The script reading a dump from stdin, as ariva-ops.mjs streams it (kubectl exec -i): a bash given one of the constant
    /// command texts below, which redirects a dump of /backup to the script's stdin. The one value, a version from the image
    /// or the source's catalog, is the shell's first argument ("$1", quoted), never part of the command text.
    /// </summary>
    private static Task<ExecResult> StreamAsync(ChartDatabase server, StreamCase stream, string version = "") =>
        server.ExecAsync(Ct, "bash", "-c", StreamCommand(stream), "ariva-db-stream", version);

    private enum StreamCase
    {
        CheckDump,
        CheckDumpGiven,
        CheckDumpOversized,
        Restore
    }

    private static string StreamCommand(StreamCase stream) => stream switch
    {
        StreamCase.CheckDump => "bash " + ChartDatabase.ScriptPath + " check-dump --input - < " + DumpPath,
        StreamCase.CheckDumpGiven => "bash " + ChartDatabase.ScriptPath + " check-dump --input - --timescaledb-version \"$1\" < " + DumpPath,
        StreamCase.CheckDumpOversized => "bash " + ChartDatabase.ScriptPath + " check-dump --input - < " + OversizedDumpPath,
        StreamCase.Restore => "bash " + ChartDatabase.ScriptPath + " restore --input - --dump-checked --timescaledb-version \"$1\" < " + DumpPath,
        _ => throw new ArgumentOutOfRangeException(nameof(stream))
    };

    private enum Query
    {
        ShippedVersions,
        InstalledVersions,
        InstalledTimescale,
        PreviousTimescale,
        PreviousToolkit,
        ListedTimescaleVersions,
        SeedTimeSeries,
        TimescaleState,
        TargetState,
        Restoring,
        ExtensionPlaces,
        RecordedVersion,
        RecordVersion,
        HideVersion,
        ShowVersion,
        PadMetadata,
        UnpadMetadata,
        PlantLower,
        UnplantLower,
        PlantCollisions
    }

    private static string Sql(Query query) => query switch
    {
        Query.ShippedVersions => "SELECT string_agg(name || '=' || default_version, ';' ORDER BY name) FROM pg_available_extensions WHERE name IN ('timescaledb', 'timescaledb_toolkit')",
        Query.InstalledVersions => "SELECT string_agg(name || '=' || installed_version, ';' ORDER BY name) FROM pg_available_extensions WHERE name IN ('timescaledb', 'timescaledb_toolkit')",
        Query.InstalledTimescale => "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'",
        Query.PreviousTimescale => """
            SELECT p.source FROM pg_extension_update_paths('timescaledb') p
              JOIN pg_available_extensions a ON a.name = 'timescaledb' AND p.target = a.default_version
              JOIN pg_available_extension_versions v ON v.name = 'timescaledb' AND v.version = p.source
             WHERE p.path IS NOT NULL AND p.source ~ '^[0-9]+(\.[0-9]+)*$'
             ORDER BY string_to_array(p.source, '.')::int[] DESC LIMIT 1
            """,
        Query.PreviousToolkit => """
            SELECT p.source FROM pg_extension_update_paths('timescaledb_toolkit') p
              JOIN pg_available_extensions a ON a.name = 'timescaledb_toolkit' AND p.target = a.default_version
              JOIN pg_available_extension_versions v ON v.name = 'timescaledb_toolkit' AND v.version = p.source
             WHERE p.path IS NOT NULL AND p.source ~ '^[0-9]+(\.[0-9]+)*$'
             ORDER BY string_to_array(p.source, '.')::int[] DESC LIMIT 1
            """,
        Query.ListedTimescaleVersions => "SELECT string_agg(version, ' ' ORDER BY version) FROM pg_available_extension_versions WHERE name = 'timescaledb'",
        // As BackupRestoreTests seeds them: queue minutes, and sensing events two to four days old whose chunks are compressed.
        Query.SeedTimeSeries => """
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, entries, exits, waits, updated_on, queue_length)
            SELECT 'DMO/IT-VERSION', t, 12, 3, 2, 1, now(), 4
              FROM generate_series(date_trunc('minute', now()) - interval '3 days', date_trunc('minute', now()) - interval '1 minute', interval '1 minute') t;
            INSERT INTO sensing_event (time_utc, site_code, queue_zone_name, kind, batch_id, ordinal, device_id, device_code, received_utc, flags, commissioned, track_id, x, y)
            SELECT t, 'DMO', 'IT-VERSION', 'Track', gen_random_uuid(), 0, gen_random_uuid(), 'S-01', t, 0, true, 'trk', 1.0, 2.0
              FROM generate_series(now() - interval '4 days', now() - interval '2 days', interval '1 minute') t;
            SELECT count(compress_chunk(c, true)) FROM show_chunks('sensing_event', older_than => interval '1 day') c;
            """,
        Query.TimescaleState => """
            SELECT (SELECT count(*) FROM timescaledb_information.hypertables) || ' hypertables, '
                || (SELECT count(*) FROM timescaledb_information.chunks WHERE hypertable_name = 'sensing_event' AND is_compressed) || ' compressed sensing chunks, '
                || (SELECT count(*) FROM sensing_event) || ' sensing events, '
                || (SELECT count(*) FROM queue_minute) || ' queue minutes, jobs '
                || (SELECT string_agg(proc_name || ':' || hypertable_name, ',' ORDER BY proc_name, hypertable_name) FROM timescaledb_information.jobs WHERE hypertable_name IS NOT NULL)
            """,
        Query.TargetState => """
            SELECT (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb') || ' ' || (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb_toolkit')
                || ', schema_version ' || (to_regclass('public.schema_version') IS NOT NULL)
                || ', group roles ' || (SELECT count(*) FROM pg_roles WHERE rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader'))
                || ', relations in public ' || (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public')
                || ', restoring ' || current_setting('timescaledb.restoring')
            """,
        Query.Restoring => "SELECT current_setting('timescaledb.restoring')",
        Query.ExtensionPlaces => """
            SELECT string_agg(e.extname || ' ' || n.nspname || ' ' || r.rolname, ', ' ORDER BY e.extname)
              FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace JOIN pg_roles r ON r.oid = e.extowner
             WHERE e.extname IN ('timescaledb', 'timescaledb_toolkit')
            """,
        Query.RecordedVersion => "SELECT value FROM _timescaledb_catalog.metadata WHERE key = 'timescaledb_version'",
        Query.RecordVersion => "UPDATE _timescaledb_catalog.metadata SET value = @version WHERE key = 'timescaledb_version'",
        // A dump that records no version: the row renamed for one backup (the key is the table's primary key).
        Query.HideVersion => "UPDATE _timescaledb_catalog.metadata SET key = 'arv244_hidden_version' WHERE key = 'timescaledb_version'",
        Query.ShowVersion => "UPDATE _timescaledb_catalog.metadata SET key = 'timescaledb_version' WHERE key = 'arv244_hidden_version'",
        // Metadata rows past the 64 KiB the script reads of them (pg_dump carries every row but uuid).
        Query.PadMetadata => "INSERT INTO _timescaledb_catalog.metadata (key, value, include_in_telemetry) VALUES ('arv244_padding', repeat('x', 70000), false)",
        Query.UnpadMetadata => "DELETE FROM _timescaledb_catalog.metadata WHERE key = 'arv244_padding'",
        // As the restore check's tests plant it: a function under a system name, which the extensions' update refuses.
        Query.PlantLower => "CREATE FUNCTION public.lower(character varying) RETURNS text LANGUAGE sql IMMUTABLE AS $$ SELECT $1::text $$",
        Query.UnplantLower => "DROP FUNCTION public.lower(character varying)",
        // What CREATE EXTENSION could collide with in the empty database, as its owner (the migration login) can create it.
        Query.PlantCollisions => """
            CREATE FUNCTION public.arv244_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1';
            CREATE TYPE public.arv244_kind AS ENUM ('a');
            CREATE TYPE public.arv244_pair AS (a integer);
            CREATE SCHEMA arv244_extra;
            ALTER DATABASE ariva SET work_mem = '8MB';
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(query))
    };

    private static async Task<string> ScalarAsync(string connectionString, Query query)
    {
        var sql = Sql(query);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Query) above
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (string)await command.ExecuteScalarAsync(Ct);
    }

    private static async Task ExecuteAsync(string connectionString, Query query, string version = null)
    {
        var sql = Sql(query);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Query) above; the version is a parameter
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        if (query == Query.RecordVersion)
            command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(Ct);
    }

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)+$")]
    private static partial Regex DottedVersion();

    #endregion
}
