using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Api.Cronz.Jobs;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TickerQ.Utilities.Base;

namespace Ariva.UnitTests.Jobs;

/// <summary>
/// ARV-207: the key lifecycle job in Ariva.Api.Cronz. It runs daily at 03:10 UTC, after the re-protection job (02:40) has moved the
/// stored secrets; it reads and writes the database only, through script 0072's function (no HTTP client can reach it); its
/// retention cannot go below the function's floor of 30 days; the Cronz host resolves it; a disabled job never connects.
/// Integration KeyLifecycleTests runs it against PostgreSQL.
/// </summary>
public sealed partial class KeyLifecycleJobsTests
{
    [Fact]
    public void Job_Should_RunDailyAt0310AfterTheReprotection_When_Scheduled()
    {
        var function = typeof(KeyLifecycleJobs).GetMethod(nameof(KeyLifecycleJobs.RunAsync)).CustomAttributes
            .Single(a => a.AttributeType == typeof(TickerFunctionAttribute));
        var reprotect = typeof(SecretJobs).GetMethod(nameof(SecretJobs.ReprotectAsync)).CustomAttributes
            .Single(a => a.AttributeType == typeof(TickerFunctionAttribute));

        function.ConstructorArguments.Select(a => a.Value).Should().StartWith([KeyLifecycleJobs.Lifecycle, "0 10 3 * * *"]);
        reprotect.ConstructorArguments[1].Value.Should().Be("0 40 2 * * *", "the keys are purged half an hour after the values moved off them");
    }

    [Fact]
    public void Round_Should_TakeNoHttpClient_When_Constructed()
    {
        foreach (var type in new[] { typeof(KeyLifecycleRound), typeof(KeyLifecycleJobs) })
        {
            var parameters = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
            parameters.Should().NotContain(t => typeof(HttpClient).IsAssignableFrom(t) || t == typeof(IHttpClientFactory) || t.Namespace == "System.Net.Http", type.Name);
        }
    }

    [Theory]
    [InlineData("Cronz:KeyLifecycle:ExpiredKeyRetentionDays", "29")]
    [InlineData("Cronz:KeyLifecycle:ExpiredKeyRetentionDays", "3651")]
    [InlineData("Cronz:KeyLifecycle:BatchSize", "0")]
    [InlineData("Cronz:KeyLifecycle:BatchSize", "1001")]
    [InlineData("Cronz:KeyLifecycle:MaxBatchesPerRun", "0")]
    [InlineData("Cronz:KeyLifecycle:MaxBatchesPerRun", "101")]
    public void AddArivaKeyLifecycleJob_Should_RefuseTheHost_When_TheSettingsAreOutOfRange(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { [key] = value }).Build();

        FluentActions.Invoking(() => new ServiceCollection().AddArivaKeyLifecycleJob(configuration))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    [Fact]
    public void Settings_Should_KeepTheFunctionsBounds_When_Defaulted()
    {
        // Script 0072 refuses less than 30 days and more than 1,000 keys a batch: the settings' bounds are the function's.
        var settings = new KeyLifecycleSettings();
        settings.ExpiredKeyRetentionDays.Should().Be(30, "Proposed: 30 days after a key expired (key-management.md section 3.8)");
        settings.BatchSize.Should().BeLessThanOrEqualTo(KeyLifecycleSettings.MaxBatchSize);
        settings.Enabled.Should().BeTrue();
        var script = SqlScriptCatalog.Embedded().Single(s => s.Name == "0072_data_protection_key_retention.sql").Sql;
        script.Should().Contain($"p_after_days < {KeyLifecycleSettings.MinRetentionDays} OR p_after_days > {KeyLifecycleSettings.MaxRetentionDays}")
            .And.Contain($"p_limit < 1 OR p_limit > {KeyLifecycleSettings.MaxBatchSize}");
        script.Should().Contain("REVOKE ALL ON FUNCTION ariva_data_protection_key_purge(integer, integer) FROM PUBLIC")
            .And.NotContain("GRANT DELETE", "the runtime role gets no DELETE on the key ring, only the function");
    }

    [Fact]
    public async Task Cronz_Should_ResolveWhatTheKeyLifecycleJobRunsWith_When_ItRuns()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Cronz);
        await using var scope = app.Services.CreateAsyncScope();

        app.Services.GetService<KeyLifecycleRound>().Should().NotBeNull();
        app.Services.GetRequiredService<KeyLifecycleSettings>().Enabled.Should().BeTrue();
        app.Services.GetService<KeyAgeMetrics>().Should().NotBeNull("every host reports its keys' age; Cronz adds the database's");
        ActivatorUtilities.CreateInstance<KeyLifecycleJobs>(scope.ServiceProvider).Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_Should_NeverConnect_When_TheJobIsDisabled()
    {
        // Nothing listens on the port: a run that connected would fail.
        var round = new KeyLifecycleRound(new DatabaseSettings { Host = "127.0.0.1", Port = 1 }, new KeyLifecycleSettings(), NullLogger<KeyLifecycleRound>.Instance);
        var job = new KeyLifecycleJobs(round, new KeyLifecycleSettings { Enabled = false }, NullLogger<KeyLifecycleJobs>.Instance);

        var run = () => job.RunAsync(new TickerFunctionContext(), TestContext.Current.CancellationToken);

        await run.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_Should_ReportARunThatFailed_When_TheDatabaseIsOutOfReach()
    {
        // The review's finding L7: a job that stopped purging is measured (ariva.keyring.problems, outcome run: 1), and TickerQ still
        // records the failure; a run that finishes reports 0.
        var round = new KeyLifecycleRound(new DatabaseSettings { Host = "127.0.0.1", Port = 1, ConnectTimeoutSeconds = 2 }, new KeyLifecycleSettings(),
            NullLogger<KeyLifecycleRound>.Instance);
        using var metrics = new KeyAgeMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var job = new KeyLifecycleJobs(round, new KeyLifecycleSettings(), NullLogger<KeyLifecycleJobs>.Instance, metrics: metrics);

        var run = () => job.RunAsync(new TickerFunctionContext(), TestContext.Current.CancellationToken);

        await run.Should().ThrowAsync<Exception>();
        var failed = reader.Observe(KeyAgeMetrics.ProblemsName).Should().ContainSingle().Subject;
        failed.Value.Should().Be(1);
        failed.Tags["outcome"].Should().Be(KeyAgeMetrics.Run);
        metrics.Reported(new KeyLifecycleRun(new KeyRingRetention(0, 1, 0, 0), new Dictionary<string, DateTime>()), KeyAgeMetrics.RunFreshFor);
        reader.Observe(KeyAgeMetrics.ProblemsName).Should().ContainSingle().Which.Value.Should().Be(0);
    }

    [Fact]
    public void Purge_Should_HoldTheKeyOfEveryLongLivedProtectedColumn_When_TheScriptsCreateOne()
    {
        // The review's finding M4: a Data Protection payload is stored in a column named *_protected. Every such column the scripts
        // create is one the purge (script 0072) holds keys for, or a short-lived one read within a bound far below the 30-day floor;
        // the re-protection job moves the same columns, and the upgrade check of ARV-211 reads the same columns. A new column of a
        // long-lived secret without its place in all three fails here.
        var scripts = SqlScriptCatalog.Embedded();
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            var sql = SqlComment().Replace(script.Sql, string.Empty);
            foreach (Match table in CreateTable().Matches(sql))
            {
                foreach (Match column in ColumnLine().Matches(table.Groups["body"].Value))
                    created.Add($"{table.Groups["table"].Value.Trim('"')}.{column.Groups["column"].Value}");
            }

            foreach (Match alter in AlterTable().Matches(sql))
            {
                foreach (Match column in AddColumn().Matches(alter.Groups["body"].Value))
                    created.Add($"{alter.Groups["table"].Value.Trim('"')}.{column.Groups["column"].Value}");
            }
        }

        static HashSet<string> Named(Regex pattern, string text) =>
            [.. pattern.Matches(text).Select(m => $"{m.Groups["table"].Value.Trim('"')}.{m.Groups["column"].Value}")];
        var purged = Named(PurgedColumn(), scripts.Single(s => s.Name == "0072_data_protection_key_retention.sql").Sql);
        var upgrade = Named(PurgedColumn(), (string)typeof(KeyRingUpgradeCheck).GetField("Count", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) ?? string.Empty);
        var reprotected = Named(ReprotectedColumn(),
            File.ReadAllText(Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Infra", "DataProtection", "SecretReprotection.cs")));
        // Read only within Auth:Sessions:RefreshGraceSeconds of its use (30 s by default; no bound is enforced on it yet, a sign-in
        // setting under the owner's freeze: key-management.md section 6, item 27).
        var shortLived = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
        {
            ["refresh_token.successor_protected"] = TimeSpan.FromSeconds(new SessionSettings().RefreshGraceSeconds)
        };

        created.Should().Contain(["user.totp_secret_protected", "integration_client.totp_secret_protected", "outbound_endpoint.secret_protected",
            "refresh_token.successor_protected"], "the scripts' protected columns are found");
        created.Except(shortLived.Keys).Should().BeEquivalentTo(purged, "every long-lived protected column holds its key against the purge");
        reprotected.Should().BeEquivalentTo(purged, "the re-protection job moves exactly the columns the purge holds keys for");
        upgrade.Should().BeEquivalentTo(purged, "the upgrade check reads the same columns");
        shortLived.Values.Should().AllSatisfy(read => read.Should().BeLessThan(TimeSpan.FromDays(KeyLifecycleSettings.MinRetentionDays)));
    }

    [GeneratedRegex("-{2}[^\n]*")]
    private static partial Regex SqlComment();

    [GeneratedRegex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?(?<table>""?[a-z_][a-z0-9_]*""?)\s*\((?<body>.*?)\);", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^\s*(?<column>[a-z_][a-z0-9_]*_protected)\s", RegexOptions.Multiline)]
    private static partial Regex ColumnLine();

    [GeneratedRegex(@"ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?(?:public\.)?(?<table>""?[a-z_][a-z0-9_]*""?)\s(?<body>[^;]*);", RegexOptions.IgnoreCase)]
    private static partial Regex AlterTable();

    [GeneratedRegex(@"ADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<column>[a-z_][a-z0-9_]*_protected)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddColumn();

    // The purge's and the upgrade check's own form: SELECT u.totp_secret_protected ... FROM public."user" u (the check names the kind first).
    [GeneratedRegex(@"SELECT\s+(?:'[a-z]+'(?:\s+AS\s+kind)?,\s+)?(?<alias>[a-z])\.(?<column>[a-z_]+_protected)\b[^\n]*?FROM\s+public\.(?<table>""?[a-z_]+""?)\s+\k<alias>\b")]
    private static partial Regex PurgedColumn();

    // SecretReprotection's form: SELECT id AS "Id", totp_secret_protected AS "Value" FROM "user"
    [GeneratedRegex(@"SELECT id AS ""Id"", (?<column>[a-z_]+_protected) AS ""Value"" FROM (?<table>""?[a-z_]+""?)")]
    private static partial Regex ReprotectedColumn();
}
