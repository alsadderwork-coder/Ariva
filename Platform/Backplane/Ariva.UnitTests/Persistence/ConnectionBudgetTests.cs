using System.Globalization;
using System.Text.RegularExpressions;
using Ariva.Infra.Settings;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-232 (CWE-400, CWE-770): every host names its database connections and bounds its runtime pool, with the same pool in its own
/// settings file and in the chart, and one replica of every host fits PostgreSQL's default max_connections (100, 3 of them kept for
/// superusers), so the lab, CI, docker-compose and the AppHost need no change to the server. The chart refuses a release whose
/// replicas, with a rolling update's extra pods, need more than its server allows (validate.yaml; chart-security.mjs proves the
/// refusal).
/// </summary>
public sealed partial class ConnectionBudgetTests
{
    #region Constants

    /// <summary>PostgreSQL's default max_connections less its superuser_reserved_connections.</summary>
    private const int DefaultServerAllows = 100 - 3;

    /// <summary>The chart's defaults for the migration job's connections and the operators' share (values.yaml databaseConnections).</summary>
    private const int MigrationAndOperators = 2 + 5;

    #endregion

    #region Data

    public static TheoryData<string, string> Hosts => new()
    {
        { "Ariva.Api.Main", "main" },
        { "Ariva.Api.Ingest", "ingest" },
        { "Ariva.Api.Stream", "stream" },
        { "Ariva.Api.Cronz", "cronz" },
        { "Ariva.Api.Integration", "integration" }
    };

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Host_Should_NameItsConnectionsAndBoundItsPool_When_ItsSettingsAreRead(string project, string shortName)
    {
        var settings = HostSettings(project);

        settings.ApplicationName.Should().Be($"ariva-{shortName}", "pg_stat_activity tells the hosts apart");
        settings.PoolProblems().Should().BeEmpty();
        settings.MaxPoolSize.Should().Be(ChartPools()[shortName], "the chart passes each host the pool its own settings file names");
    }

    [Fact]
    public void Hosts_Should_FitPostgreSqlsDefaultLimit_When_OneReplicaOfEachRuns()
    {
        var pools = ChartPools();

        // Every host's runtime pool, api-main's validation reader, api-stream's desk term reads, the migration job and the operators.
        var needed = pools.Values.Sum() + ValidationReaderSettings.MaxPoolSize + DatabaseSettings.DeskTermPoolSize + MigrationAndOperators;

        pools.Keys.Should().BeEquivalentTo("main", "ingest", "stream", "cronz", "integration");
        needed.Should().BeLessThanOrEqualTo(DefaultServerAllows, "one replica of each host runs on a PostgreSQL left at its defaults");
    }

    [Fact]
    public void Chart_Should_CountTheFixedPoolsAsTheHostsHoldThem_When_AddingUpTheConnections()
    {
        // validate.yaml adds, as literals, api-main's validation reader (4) and api-stream's desk term reads (2) to each of their
        // replicas; no other host holds either pool.
        var validate = File.ReadAllText(RepositoryPaths.Resolve("Platform/Cloud/Ariva.K8s/Helm/Charts/platform/templates/validate.yaml"));

        validate.Should().Contain("$fixedPools := dict \"main\" 4 \"stream\" 2");
        DatabaseSettings.DeskTermPoolSize.Should().Be(2);
        ValidationReaderSettings.MaxPoolSize.Should().Be(4);
    }

    #endregion

    #region Helpers

    /// <summary>A host's database settings from the files it layers first (the base file, then its own service file).</summary>
    private static DatabaseSettings HostSettings(string project) =>
        DatabaseSettings.FromConfiguration(new ConfigurationBuilder()
            .AddJsonFile(RepositoryPaths.Resolve("Platform/Backplane/Ariva.Api.Common/appsettings.base.json"))
            .AddJsonFile(RepositoryPaths.Resolve($"Platform/Backplane/{project}/appsettings.service.json"))
            .Build());

    /// <summary>The pools under <c>databaseConnections.poolSize</c> of the chart's values.yaml, by host short name.</summary>
    private static Dictionary<string, int> ChartPools()
    {
        var lines = File.ReadAllLines(RepositoryPaths.Resolve("Platform/Cloud/Ariva.K8s/Helm/Charts/platform/values.yaml"));
        var pools = new Dictionary<string, int>(StringComparer.Ordinal);
        var inConnections = false;
        var inPools = false;
        foreach (var line in lines)
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                inConnections = line.StartsWith("databaseConnections:", StringComparison.Ordinal);
                inPools = false;
                continue;
            }

            if (inConnections && line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'))
                inPools = line.Trim() == "poolSize:";
            else if (inPools && ChartPool().Match(line) is { Success: true } pool)
                pools[pool.Groups[1].Value] = int.Parse(pool.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return pools;
    }

    [GeneratedRegex(@"^    ([a-z]+): (\d+)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ChartPool();

    #endregion
}
