using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Di;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-062: the TimescaleDB chart's first start, reproduced on the chart's own inputs. The image (by the digest in
/// Charts/timescaledb/values.yaml), the initdb arguments (statefulset.yaml) and the init script (init-configmap.yaml)
/// are read from the chart, and the container runs as the pod does: uid 1000, read-only root filesystem, every
/// capability dropped, writable only where the pod mounts emptyDirs and the data volume. Then: no login works without a
/// password over TCP, loopback included (a port-forward arrives there); the migration login owns the database with
/// CREATEROLE and is not a superuser; the real migration (api-main --migrate) runs as that login; and the runtime login
/// it creates cannot change the schema.
/// </summary>
public sealed class DatabaseChartBootstrapTests : IAsyncLifetime
{
    private const string MigrationLogin = "ariva";
    private const string Database = "ariva";
    private static readonly string SuperuserPassword = "su-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    private static readonly string MigrationPassword = "mig-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IContainer _container;
    private string _initScript;

    private static string Chart(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ariva.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Ariva.slnx not found above the test output.");
        return File.ReadAllText(Path.Combine(root, "Platform", "Cloud", "Ariva.K8s", "Helm", "Charts", "timescaledb", relative));
    }

    /// <summary>The init script as the ConfigMap carries it: the block under its key, without the YAML indentation.</summary>
    private static string InitScript()
    {
        var lines = Chart("templates/init-configmap.yaml").Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd() == "  100_ariva_owner.sh: |");
        start.Should().BeGreaterThan(0, "the ConfigMap carries the init script");
        var script = new StringBuilder();
        foreach (var line in lines.Skip(start + 1))
        {
            if (line.Length > 0 && !line.StartsWith("    ", StringComparison.Ordinal))
                break;
            script.Append(line.Length >= 4 ? line[4..] : line).Append('\n');
        }

        return script.ToString();
    }

    public async ValueTask InitializeAsync()
    {
        var values = Chart("values.yaml");
        var repository = Regex.Match(values, @"^\s+repository:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;
        var digest = Regex.Match(values, @"^\s+digest:\s*(sha256:[0-9a-f]{64})", RegexOptions.Multiline).Groups[1].Value;
        var initdbArgs = Regex.Match(Chart("templates/statefulset.yaml"), @"name: POSTGRES_INITDB_ARGS\s+value: ""([^""]+)""").Groups[1].Value;
        digest.Should().NotBeEmpty();
        initdbArgs.Should().NotBeEmpty();

        // The pod mounts the script from a ConfigMap; here a read-only bind mount of the same text.
        _initScript = Path.Combine(Path.GetTempPath(), $"ariva-init-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(_initScript, InitScript(), Ct);
        File.SetUnixFileMode(_initScript, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _container = new ContainerBuilder($"{repository}@{digest}")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", SuperuserPassword)
            .WithEnvironment("POSTGRES_DB", Database)
            .WithEnvironment("POSTGRES_INITDB_ARGS", initdbArgs)
            .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
            .WithEnvironment("ARIVA_MIGRATION_LOGIN", MigrationLogin)
            .WithEnvironment("ARIVA_MIGRATION_PASSWORD", MigrationPassword)
            .WithBindMount(_initScript, "/docker-entrypoint-initdb.d/100_ariva_owner.sh", AccessMode.ReadOnly)
            .WithPortBinding(5432, true)
            .WithCreateParameterModifier(parameters =>
            {
                parameters.User = "1000:1000";
                parameters.HostConfig.ReadonlyRootfs = true;
                parameters.HostConfig.CapDrop = ["ALL"];
                parameters.HostConfig.SecurityOpt = ["no-new-privileges"];
                parameters.HostConfig.ShmSize = 256L * 1024 * 1024;
                // The pod's writable paths: the data volume, /tmp and the socket directory.
                parameters.HostConfig.Tmpfs = new Dictionary<string, string>
                {
                    ["/home/postgres/pgdata"] = "size=1g,uid=1000,gid=1000,mode=0700",
                    ["/tmp"] = "size=64m",
                    ["/var/run/postgresql"] = "size=8m,uid=1000,gid=1000"
                };
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("PostgreSQL init process complete"))
            .Build();
        await _container.StartAsync(Ct);
        await WaitForServerAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
        if (_initScript is not null)
            File.Delete(_initScript);
    }

    private string ConnectionString(string username, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = _container.Hostname,
        Port = _container.GetMappedPublicPort(5432),
        Database = Database,
        Username = username,
        Password = password,
        Pooling = false,
        Timeout = 10
    }.ConnectionString;

    private async Task WaitForServerAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString("postgres", SuperuserPassword));
                await connection.OpenAsync(Ct);
                return;
            }
            catch (NpgsqlException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(500, Ct);
            }
        }
    }

    private enum Query
    {
        One,
        IsSuperuser,
        CanCreateRoles,
        DatabaseOwner,
        TimescaleExtension,
        Telemetry,
        AppliedScripts,
        CreateTable
    }

    // A switch over string literals keeps every command text constant (CA2100).
    private static string Sql(Query query) => query switch
    {
        Query.One => "SELECT 1",
        Query.IsSuperuser => "SELECT rolsuper FROM pg_roles WHERE rolname = current_user",
        Query.CanCreateRoles => "SELECT rolcreaterole FROM pg_roles WHERE rolname = current_user",
        Query.DatabaseOwner => "SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()",
        Query.TimescaleExtension => "SELECT extname::text FROM pg_extension WHERE extname = 'timescaledb'",
        Query.Telemetry => "SHOW timescaledb.telemetry_level",
        Query.AppliedScripts => "SELECT count(*) FROM schema_version",
        Query.CreateTable => "CREATE TABLE it_not_allowed (id int)",
        _ => throw new ArgumentOutOfRangeException(nameof(query))
    };

    private async Task<T> ScalarAsync<T>(string connectionString, Query query)
    {
        var sql = Sql(query);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Query) above
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)await command.ExecuteScalarAsync(Ct);
    }

    [Fact(Timeout = 300_000)]
    public async Task FirstStart_Should_RefusePasswordFreeLoginsAndHandTheDatabaseToANonSuperuserOwner_When_RunLikeThePod()
    {
        (await _container.ExecAsync(["id", "-u"], Ct)).Stdout.Trim().Should().Be("1000");

        // Over TCP without a password: refused from outside the pod and on loopback inside it (where a port-forward lands).
        var outside = async () => await ScalarAsync<int>(ConnectionString("postgres", null), Query.One);
        await outside.Should().ThrowAsync<NpgsqlException>();
        var loopback = await _container.ExecAsync(["psql", "-w", "-h", "127.0.0.1", "-U", "postgres", "-d", Database, "-c", "SELECT 1"], Ct);
        loopback.ExitCode.Should().NotBe(0, $"loopback TCP must need a password ({loopback.Stderr.Trim()})");
        var wrong = async () => await ScalarAsync<int>(ConnectionString(MigrationLogin, "not-the-password-123"), Query.One);
        await wrong.Should().ThrowAsync<PostgresException>();

        // The migration login: owner, CREATEROLE, never superuser; the extension was created by the superuser.
        var owner = ConnectionString(MigrationLogin, MigrationPassword);
        (await ScalarAsync<bool>(owner, Query.IsSuperuser)).Should().BeFalse();
        (await ScalarAsync<bool>(owner, Query.CanCreateRoles)).Should().BeTrue();
        (await ScalarAsync<string>(owner, Query.DatabaseOwner)).Should().Be(MigrationLogin);
        (await ScalarAsync<string>(owner, Query.TimescaleExtension)).Should().Be("timescaledb");
        (await ScalarAsync<string>(owner, Query.Telemetry)).Should().Be("off");

        // The release's migration job, as that login.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = _container.Hostname,
            ["Database:Port"] = _container.GetMappedPublicPort(5432).ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = Database,
            ["Database:Migration:Username"] = MigrationLogin,
            ["Database:Migration:Password"] = MigrationPassword,
            ["Database:Username"] = "ariva_app",
            ["Database:Password"] = RuntimePassword
        }).Build();
        (await DatabaseMigration.RunAsync(configuration, Ct)).Should().Be(0, "the migration runs as the non-superuser owner");

        var runtime = ConnectionString("ariva_app", RuntimePassword);
        (await ScalarAsync<long>(runtime, Query.AppliedScripts)).Should().BeGreaterThan(0);
        var ddl = async () => await ScalarAsync<object>(runtime, Query.CreateTable);
        (await ddl.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }
}
