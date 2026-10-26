using System.Globalization;
using System.Text.RegularExpressions;
using Ariva.Infra.Alerting;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Alerting;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-111a: the retention periods are migration-job parameters (<c>Database:Retention</c>). Their defaults are the scripts'
/// values and the Proposed periods; every period is checked against its bounds (which follow what reads the data: the ingest's
/// maximum age, the desk feed's catch-up, the alert backtest's history, the report delivery round's look-back); every
/// retention policy a script adds is a parameter; script 0054 accepts exactly the relational data sets and the same floor; the
/// committed settings and every environment's Helm values are within the bounds; and the migration job stops before it
/// connects when a period is out of range.
/// </summary>
public sealed partial class RetentionSettingsTests
{
    #region Defaults and bounds

    [Fact]
    public void Defaults_Should_BeTheScriptsValuesAndTheProposedPeriods_When_NothingIsConfigured()
    {
        var settings = new RetentionSettings();

        settings.RawSensingDays.Should().Be(90, "TC-62: 90 days until the contract's dispute window is known");
        settings.DeskZoneReadingDays.Should().Be(7, "script 0040");
        settings.LaneStaffingDays.Should().Be(90, "script 0053, the decision of 2026-10-15");
        settings.AlertEmailDays.Should().Be(180, "Proposed, TC-67");
        settings.IntegrationCallDays.Should().Be(400, "Proposed, TC-68");
        settings.ReportDeliveryDays.Should().Be(400, "Proposed, TC-66");
        settings.AdvertisingPlayDays.Should().Be(400, "script 0062, Proposed with ARV-144");
        settings.Problems().Should().BeEmpty();
    }

    [Fact]
    public void Hypertables_Should_CoverEveryRetentionPolicyAScriptAdds_When_TheDefaultsApply()
    {
        // Every add_retention_policy of a shipped script is a parameter, and its default is the script's value: a fresh database
        // migrated with the defaults keeps exactly the policies the scripts gave.
        var policies = ScriptPolicies();
        var parameters = new RetentionSettings().Hypertables().ToDictionary(h => h.Table, h => h.Days, StringComparer.Ordinal);

        policies.Should().NotBeEmpty();
        parameters.Should().BeEquivalentTo(policies, "every hypertable with a retention policy in a script is a migration-job parameter with the script's value");
    }

    [Fact]
    public void Hypertables_Should_KeepRawEventsAndDeviceHealthTogether_When_RawSensingIsSet()
    {
        var settings = new RetentionSettings { RawSensingDays = 45 };

        settings.Hypertables().Where(h => h.Days == 45).Select(h => h.Table)
            .Should().BeEquivalentTo(["sensing_event", "sensing_batch", "device_health_event"], "a replay needs the events and the devices' health of the same period");
    }

    [Fact]
    public void Bounds_Should_FollowWhatReadsTheData_When_Checked()
    {
        RetentionSettings.MinRawSensingDays.Should().BeGreaterThan((int)SensingArchive.Window.TotalDays,
            "raw events can arrive for 3 days (the ingest's maximum age and the sensing topics' retention)");
        RetentionSettings.MinLaneStaffingDays.Should().Be((int)SvcAlertRules.BacktestHistory.TotalDays, "the open desks alert's backtest reads 90 days");
        RetentionSettings.MinLaneStaffingDays.Should().Be((int)AlertInputs.StaffingHistory.TotalDays);
        RetentionSettings.MinRelationalDays.Should().BeGreaterThan(3, "a report delivery is owed for 3 days at most: a deleted row must never be owed again");
    }

    public static TheoryData<string, int> OutOfRange => new()
    {
        { nameof(RetentionSettings.RawSensingDays), 6 },
        { nameof(RetentionSettings.RawSensingDays), 3_651 },
        { nameof(RetentionSettings.DeskZoneReadingDays), 1 },
        { nameof(RetentionSettings.DeskZoneReadingDays), 91 },
        { nameof(RetentionSettings.LaneStaffingDays), 89 },
        { nameof(RetentionSettings.LaneStaffingDays), 3_651 },
        { nameof(RetentionSettings.AlertEmailDays), 29 },
        { nameof(RetentionSettings.AlertEmailDays), 3_651 },
        { nameof(RetentionSettings.IntegrationCallDays), 0 },
        { nameof(RetentionSettings.IntegrationCallDays), -400 },
        { nameof(RetentionSettings.ReportDeliveryDays), 29 },
        { nameof(RetentionSettings.ReportDeliveryDays), 10_000 },
        { nameof(RetentionSettings.AdvertisingPlayDays), 29 },
        { nameof(RetentionSettings.AdvertisingPlayDays), 3_651 }
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Problems_Should_NameThePeriod_When_ItIsOutOfRange(string key, int days)
    {
        var settings = Bind((key, days.ToString(CultureInfo.InvariantCulture)));

        settings.Problems().Should().ContainSingle().Which.Should().StartWith($"Database:Retention:{key} ");
        FluentActions.Invoking(settings.EnsureValid).Should().Throw<InvalidOperationException>().WithMessage($"*Database:Retention:{key}*");
    }

    [Fact]
    public void Problems_Should_BeEmpty_When_EveryPeriodIsAtItsBounds()
    {
        Bind(("RawSensingDays", "7"), ("DeskZoneReadingDays", "2"), ("LaneStaffingDays", "90"), ("AlertEmailDays", "30"), ("IntegrationCallDays", "30"),
            ("ReportDeliveryDays", "30"), ("AdvertisingPlayDays", "30")).Problems().Should().BeEmpty();
        Bind(("RawSensingDays", "3650"), ("DeskZoneReadingDays", "90"), ("LaneStaffingDays", "3650"), ("AlertEmailDays", "3650"), ("IntegrationCallDays", "3650"),
            ("ReportDeliveryDays", "3650"), ("AdvertisingPlayDays", "3650")).Problems().Should().BeEmpty();
    }

    [Fact]
    public void DatabaseSettings_Should_BindTheRetentionSection_When_Configured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Retention:RawSensingDays"] = "30",
            ["Database:Retention:AlertEmailDays"] = "60"
        }).Build();

        var settings = DatabaseSettings.FromConfiguration(configuration);

        settings.Retention.RawSensingDays.Should().Be(30);
        settings.Retention.AlertEmailDays.Should().Be(60);
        settings.Retention.IntegrationCallDays.Should().Be(400, "an unset period keeps its default");
        settings.Retention.DataSets().Select(d => d.DataSet).Should().Equal(RetentionSettings.DataSetNames);
    }

    #endregion

    #region Script 0054

    [Fact]
    public void Script0054_Should_AcceptExactlyTheDataSetsAndTheFloor_When_ItCreatesThePeriods()
    {
        var script = SqlScriptCatalog.Embedded().Single(s => s.Name == "0054_retention_period.sql").Sql;

        var dataSets = DataSetCheck().Match(script);
        dataSets.Success.Should().BeTrue();
        dataSets.Groups[1].Value.Split(',').Select(v => v.Trim().Trim('\'')).Should().Equal(RetentionSettings.DataSetNames);
        script.Should().Contain($"days BETWEEN {RetentionSettings.MinRelationalDays} AND {RetentionSettings.MaxDays}");
        script.Should().Contain($"IF period < {RetentionSettings.MinRelationalDays} THEN", "the function refuses a shorter period whatever the table holds");
        script.Should().Contain("p_limit > 10000").And.Contain("SECURITY DEFINER").And.Contain("SET search_path = pg_catalog, pg_temp")
            .And.Contain("SET TimeZone = 'UTC'")
            .And.Contain("REVOKE ALL ON FUNCTION ariva_retention_purge(text, integer) FROM PUBLIC")
            .And.Contain("GRANT EXECUTE ON FUNCTION ariva_retention_purge(text, integer) TO ariva_runtime")
            .And.Contain("REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON retention_period FROM ariva_runtime");
        script.Should().NotContain("EXECUTE format", "no dynamic SQL in the purge (CWE-89)");
        script.Should().NotContain("search_path = pg_catalog, public", "every object in the body is named with its schema: public adds nothing but operators that could stand in");
        // Round 2 (N1): the runtime role cannot move report_delivery.created_utc, the column the purge reads; it keeps UPDATE of
        // the columns the delivery round marks (SvcReportDeliveries.MarkAsync) only.
        script.Should().Contain("REVOKE UPDATE ON report_delivery FROM ariva_runtime;")
            .And.Contain("GRANT UPDATE (status, attempts, reason, sent_utc) ON report_delivery TO ariva_runtime;");
        foreach (var name in RetentionSettings.DataSetNames)
            script.Should().Contain($"DELETE FROM public.{name} ", $"{name} has its own constant statement, named with its schema");
    }

    #endregion

    #region Committed settings and Helm values

    public static TheoryData<string> EnvironmentFiles =>
    [
        "appsettings.base.json",
        "appsettings.base.vm-local.json",
        "appsettings.base.k8s-dev.json",
        "appsettings.base.k8s-demo.json",
        "appsettings.base.k8s-prd.json"
    ];

    [Theory]
    [MemberData(nameof(EnvironmentFiles))]
    public void CommittedSettings_Should_HoldValidPeriods_When_Loaded(string file)
    {
        var settings = DatabaseSettings.FromConfiguration(new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, file))
            .Build());

        settings.Retention.Problems().Should().BeEmpty(file);
    }

    public static TheoryData<string> ValuesFiles => ["values-k8s-dev.yaml", "values-k8s-demo.yaml", "values-k8s-prd.yaml", "values-localk8s.yaml"];

    [Fact]
    public void HelmDefaults_Should_BeTheCodeDefaults_When_AnEnvironmentSetsNothing()
    {
        var values = HelmRetention("values.yaml");

        values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["RawSensingDays"] = "90",
            ["DeskZoneReadingDays"] = "7",
            ["LaneStaffingDays"] = "90",
            ["AlertEmailDays"] = "180",
            ["IntegrationCallDays"] = "400",
            ["ReportDeliveryDays"] = "400",
            ["AdvertisingPlayDays"] = "400"
        }, "values.yaml repeats RetentionSettings' defaults");
    }

    [Theory]
    [MemberData(nameof(ValuesFiles))]
    public void HelmValues_Should_GiveEveryEnvironmentValidPeriods_When_TheMigrationJobReadsThem(string file)
    {
        var values = HelmRetention("values.yaml");
        foreach (var (key, value) in HelmRetention(file))
            values[key] = value;

        var settings = Bind([.. values.Select(v => (v.Key, v.Value))]);

        values.Should().HaveCount(7, $"{file}: the seven periods");
        settings.Problems().Should().BeEmpty(file);
    }

    [Fact]
    public void HelmValues_Should_KeepRawSensingAtNinetyDaysInProduction_When_TC62IsOpen()
    {
        HelmRetention("values-k8s-prd.yaml").Should().Contain("RawSensingDays", "90", "TC-62 is not answered");
    }

    #endregion

    #region Migration job

    [Fact]
    public async Task MigrateAsync_Should_StopBeforeItConnects_When_APeriodIsOutOfRange()
    {
        // Nothing listens on the port: a job that tried to connect would fail with a connection error instead.
        var settings = new DatabaseSettings { Host = "127.0.0.1", Port = 1, Retention = new RetentionSettings { RawSensingDays = 3 } };
        var migrator = new DatabaseMigrator(settings, new ValidationReaderSettings(), new NHibernateSessionFactoryProvider(settings), NullLoggerFactory.Instance,
            new DatabaseEnvironment(null));

        var migrate = () => migrator.MigrateAsync(TestContext.Current.CancellationToken);

        await migrate.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("Database:Retention:RawSensingDays*");
    }

    #endregion

    #region Helpers

    [GeneratedRegex(@"data_set\s+varchar\(40\)\s+PRIMARY KEY CHECK \(data_set IN \(([^)]*)\)\)")]
    private static partial Regex DataSetCheck();

    [GeneratedRegex(@"add_retention_policy\('([a-z_]+)',\s*INTERVAL\s*'(\d+) days'\)")]
    private static partial Regex RetentionPolicy();

    [GeneratedRegex(@"^\s{4}([a-z][A-Za-z]+Days):\s*(\d+)\s*(#.*)?$")]
    private static partial Regex HelmPeriod();

    /// <summary>The retention policies the shipped scripts add, by table, in days.</summary>
    private static Dictionary<string, int> ScriptPolicies()
    {
        var policies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var script in SqlScriptCatalog.Embedded())
        {
            foreach (Match match in RetentionPolicy().Matches(script.Sql))
                policies[match.Groups[1].Value] = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return policies;
    }

    /// <summary>The periods under <c>migration.retention</c> of a values file, keyed as the job's settings are (RawSensingDays).</summary>
    private static Dictionary<string, string> HelmRetention(string file)
    {
        var lines = File.ReadAllLines(RepositoryPaths.Resolve($"Platform/Cloud/Ariva.K8s/Helm/Charts/platform/{file}"));
        var periods = new Dictionary<string, string>(StringComparer.Ordinal);
        var inMigration = false;
        var inRetention = false;
        foreach (var line in lines)
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                inMigration = line.StartsWith("migration:", StringComparison.Ordinal);
                inRetention = false;
                continue;
            }

            if (inMigration && line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'))
                inRetention = line.Trim() == "retention:";
            else if (inRetention && HelmPeriod().Match(line) is { Success: true } period)
                periods[char.ToUpperInvariant(period.Groups[1].Value[0]) + period.Groups[1].Value[1..]] = period.Groups[2].Value;
        }

        return periods;
    }

    private static RetentionSettings Bind(params (string Key, string Value)[] values) =>
        RetentionSettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => $"{RetentionSettings.SectionName}:{v.Key}", v => v.Value))
            .Build());

    #endregion
}
