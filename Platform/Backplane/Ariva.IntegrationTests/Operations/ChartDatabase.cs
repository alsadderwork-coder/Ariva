using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.UnitTests.Setup;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// A database server started as the TimescaleDB chart's StatefulSet starts one (ARV-062, ARV-126): the image by the digest
/// in Charts/timescaledb/values.yaml, the initdb arguments from statefulset.yaml and the init script from
/// init-configmap.yaml, run as uid 1000 with a read-only root filesystem, every capability dropped and the pod's writable
/// paths only. The operations script scripts/ops/ariva-db.sh is mounted read-only at <see cref="ScriptPath"/> and run with
/// <see cref="OpsAsync"/>, as an operator runs it in the pod; <see cref="BackupDirectory"/> is a folder shared with the test.
/// </summary>
internal sealed class ChartDatabase : IAsyncDisposable
{
    public const string Database = "ariva";
    public const string MigrationLogin = "ariva";
    public const string ScriptPath = "/opt/ariva-ops/ariva-db.sh";
    public const string BackupDirectory = "/backup";

    private readonly IContainer _container;
    private readonly string _initScript;

    private ChartDatabase(IContainer container, string initScript, string superuserPassword, string migrationPassword)
    {
        _container = container;
        _initScript = initScript;
        SuperuserPassword = superuserPassword;
        MigrationPassword = migrationPassword;
    }

    public string SuperuserPassword { get; }

    public string MigrationPassword { get; }

    public string Hostname => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(5432);

    /// <summary>Starts a server whose /backup is <paramref name="hostBackupDirectory"/> (writable by uid 1000).</summary>
    public static async Task<ChartDatabase> StartAsync(string hostBackupDirectory, CancellationToken ct)
    {
        var values = Chart("values.yaml");
        var repository = Regex.Match(values, @"^\s+repository:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;
        var digest = Regex.Match(values, @"^\s+digest:\s*(sha256:[0-9a-f]{64})", RegexOptions.Multiline).Groups[1].Value;
        var initdbArgs = Regex.Match(Chart("templates/statefulset.yaml"), @"name: POSTGRES_INITDB_ARGS\s+value: ""([^""]+)""").Groups[1].Value;
        if (digest.Length == 0 || initdbArgs.Length == 0)
            throw new InvalidOperationException("The chart's image digest or initdb arguments were not found.");

        var initScript = Path.Combine(Path.GetTempPath(), $"ariva-init-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(initScript, InitScript(), ct);
        File.SetUnixFileMode(initScript, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                         UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var superuserPassword = "su-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        var migrationPassword = "mig-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        var container = new ContainerBuilder($"{repository}@{digest}")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", superuserPassword)
            .WithEnvironment("POSTGRES_DB", Database)
            .WithEnvironment("POSTGRES_INITDB_ARGS", initdbArgs)
            .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
            .WithEnvironment("ARIVA_MIGRATION_LOGIN", MigrationLogin)
            .WithEnvironment("ARIVA_MIGRATION_PASSWORD", migrationPassword)
            .WithBindMount(initScript, "/docker-entrypoint-initdb.d/100_ariva_owner.sh", AccessMode.ReadOnly)
            .WithBindMount(RepositoryPaths.Resolve("scripts/ops/ariva-db.sh"), ScriptPath, AccessMode.ReadOnly)
            .WithBindMount(hostBackupDirectory, BackupDirectory, AccessMode.ReadWrite)
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
        await container.StartAsync(ct);
        var database = new ChartDatabase(container, initScript, superuserPassword, migrationPassword);
        await database.WaitForServerAsync(ct);
        return database;
    }

    /// <summary>Runs scripts/ops/ariva-db.sh in the container with these arguments (an argument array, no shell).</summary>
    public Task<ExecResult> OpsAsync(CancellationToken ct, params string[] arguments) => _container.ExecAsync(["bash", ScriptPath, .. arguments], ct);

    /// <summary>Runs a program in the container with these arguments (an argument array, no shell).</summary>
    public Task<ExecResult> ExecAsync(CancellationToken ct, params string[] command) => _container.ExecAsync(command, ct);

    public string ConnectionString(string username, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = Hostname,
        Port = Port,
        Database = Database,
        Username = username,
        Password = password,
        Pooling = false,
        Timeout = 15
    }.ConnectionString;

    /// <summary>The migration job's settings (api-main --migrate) for this server, with the given runtime login.</summary>
    public Dictionary<string, string> MigrationSettings(string runtimeLogin, string runtimePassword) => new()
    {
        ["Database:Host"] = Hostname,
        ["Database:Port"] = Port.ToString(CultureInfo.InvariantCulture),
        ["Database:Name"] = Database,
        ["Database:Migration:Username"] = MigrationLogin,
        ["Database:Migration:Password"] = MigrationPassword,
        ["Database:Username"] = runtimeLogin,
        ["Database:Password"] = runtimePassword
    };

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        File.Delete(_initScript);
    }

    private async Task WaitForServerAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString(MigrationLogin, MigrationPassword));
                await connection.OpenAsync(ct);
                return;
            }
            catch (NpgsqlException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(500, ct);
            }
        }
    }

    private static string Chart(string relative) =>
        File.ReadAllText(RepositoryPaths.Resolve("Platform/Cloud/Ariva.K8s/Helm/Charts/timescaledb/" + relative));

    /// <summary>The init script as the ConfigMap carries it: the block under its key, without the YAML indentation.</summary>
    private static string InitScript()
    {
        var lines = Chart("templates/init-configmap.yaml").Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd() == "  100_ariva_owner.sh: |");
        if (start <= 0)
            throw new InvalidOperationException("The chart's ConfigMap has no init script.");
        var script = new StringBuilder();
        foreach (var line in lines.Skip(start + 1))
        {
            if (line.Length > 0 && !line.StartsWith("    ", StringComparison.Ordinal))
                break;
            script.Append(line.Length >= 4 ? line[4..] : line).Append('\n');
        }

        return script.ToString();
    }
}
