using Ariva.Api.Cronz.Jobs;
using Ariva.Infra.Availability;
using Ariva.Infra.Retention;
using Ariva.Infra.Settings;
using Ariva.Infra.Staffing;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TickerQ.Utilities.Base;

namespace Ariva.UnitTests.Jobs;

/// <summary>
/// ARV-111a: the relational retention job in Ariva.Api.Cronz. It runs daily at 03:15 UTC under an advisory lock class of its own
/// (one replica at a time); it reads the database only (no HTTP client can reach it) and takes no period from its own
/// settings (the periods are the migration job's); its batch settings are checked at start; the Cronz host resolves it; a
/// disabled job never connects.
/// </summary>
public sealed class RetentionJobsTests
{
    [Fact]
    public void Job_Should_RunDailyAt0315_When_Scheduled()
    {
        var function = typeof(RetentionJobs).GetMethod(nameof(RetentionJobs.PurgeAsync)).CustomAttributes
            .Single(a => a.AttributeType == typeof(TickerFunctionAttribute));

        function.ConstructorArguments.Select(a => a.Value).Should().StartWith([RetentionJobs.Purge, "0 15 3 * * *"]);
    }

    [Fact]
    public void Round_Should_TakeALockClassNoOtherJobTakes_When_Run()
    {
        RetentionRound.LockClass.Should().Be(52);
        RetentionRound.LockClass.Should().NotBe(StaffingRecommendations.LockClass).And.NotBe(AvailabilityLedger.LockClass);
        new[] { 25, 26, 36, 37, 38, 41, 42, 49, 50, 51 }.Should().NotContain(RetentionRound.LockClass, "the two-key classes other locks take");
    }

    [Fact]
    public void Round_Should_TakeNoHttpClientAndNoPeriodSetting_When_Constructed()
    {
        foreach (var type in new[] { typeof(RetentionRound), typeof(RetentionJobs) })
        {
            var parameters = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
            parameters.Should().NotContain(t => typeof(HttpClient).IsAssignableFrom(t) || t == typeof(IHttpClientFactory) || t.Namespace == "System.Net.Http", type.Name);
            parameters.Should().NotContain(typeof(RetentionSettings), $"{type.Name}: the periods come from retention_period, which only the migration login writes");
        }

        typeof(RetentionJobSettings).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["Enabled", "BatchSize", "MaxBatchesPerRun"],
            "the job's own settings say how it runs, never how long anything is kept");
    }

    [Theory]
    [InlineData("Cronz:Retention:BatchSize", "99")]
    [InlineData("Cronz:Retention:BatchSize", "10001")]
    [InlineData("Cronz:Retention:MaxBatchesPerRun", "0")]
    [InlineData("Cronz:Retention:MaxBatchesPerRun", "1001")]
    public void AddArivaRetentionJob_Should_RefuseTheHost_When_TheSettingsAreOutOfRange(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { [key] = value }).Build();

        FluentActions.Invoking(() => new ServiceCollection().AddArivaRetentionJob(configuration))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    [Fact]
    public void AddArivaRetentionJob_Should_BindTheSection_When_Valid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Cronz:Retention:BatchSize"] = "100",
            ["Cronz:Retention:MaxBatchesPerRun"] = "1000"
        }).Build();
        var services = new ServiceCollection().AddArivaRetentionJob(configuration);

        var settings = services.Single(d => d.ServiceType == typeof(RetentionJobSettings)).ImplementationInstance.Should().BeOfType<RetentionJobSettings>().Subject;
        settings.BatchSize.Should().Be(100);
        settings.MaxBatchesPerRun.Should().Be(1_000);
        settings.Enabled.Should().BeTrue();
        new RetentionJobSettings().BatchSize.Should().BeLessThanOrEqualTo(RetentionJobSettings.MaxBatchSize, "script 0054 takes at most 10,000 rows a batch");
        services.Should().Contain(d => d.ServiceType == typeof(RetentionRound) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task Cronz_Should_ResolveWhatTheRetentionJobRunsWith_When_ItRuns()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Cronz);
        await using var scope = app.Services.CreateAsyncScope();

        app.Services.GetService<RetentionRound>().Should().NotBeNull();
        app.Services.GetRequiredService<RetentionJobSettings>().Enabled.Should().BeTrue();
        ActivatorUtilities.CreateInstance<RetentionJobs>(scope.ServiceProvider).Should().NotBeNull();
    }

    [Fact]
    public async Task PurgeAsync_Should_NeverConnect_When_TheJobIsDisabled()
    {
        // Nothing listens on the port: a run that connected would fail.
        var round = new RetentionRound(new DatabaseSettings { Host = "127.0.0.1", Port = 1 }, new RetentionJobSettings(), NullLogger<RetentionRound>.Instance);
        var job = new RetentionJobs(round, new RetentionJobSettings { Enabled = false }, NullLogger<RetentionJobs>.Instance);

        var purge = () => job.PurgeAsync(new TickerFunctionContext(), TestContext.Current.CancellationToken);

        await purge.Should().NotThrowAsync();
    }

    [Fact]
    public void RetentionMetrics_Should_ReportTheRunsOutcomeForADayAndTwoHours_When_ARunEnds()
    {
        // ARV-259c: the data sets that failed and those without a period; a run that failed as a whole replaces them.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new RetentionMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        Dictionary<string, double> Outcomes() => reader.Observe(RetentionMetrics.ProblemsName).ToDictionary(m => m.Tags["outcome"], m => m.Value, StringComparer.Ordinal);
        Outcomes().Should().BeEmpty("nothing before the first run");

        metrics.RunFinished(new RetentionRun(false,
        [
            new RetentionDataSetRun("email_message", 30, 10, 1, true, false),
            new RetentionDataSetRun("integration_call", 45, 0, 1, false, true),
            new RetentionDataSetRun("report_delivery", null, 0, 0, false, false)
        ]), RetentionMetrics.RunFreshFor);
        clock.Advance(TimeSpan.FromHours(26));

        Outcomes().Should().BeEquivalentTo(new Dictionary<string, double> { ["failed"] = 1, ["unset"] = 1, ["run"] = 0 });
        clock.Advance(TimeSpan.FromSeconds(1));
        Outcomes().Should().BeEmpty("a run older than a day and two hours: the job stopped running");

        metrics.RunFailed(RetentionMetrics.RunFreshFor);
        Outcomes().Should().BeEquivalentTo(new Dictionary<string, double> { ["run"] = 1 }, "what the data sets did is not known");
        metrics.RunFinished(new RetentionRun(false, [new RetentionDataSetRun("email_message", 30, 0, 1, true, false)]), RetentionMetrics.RunFreshFor);
        Outcomes().Should().BeEquivalentTo(new Dictionary<string, double> { ["failed"] = 0, ["unset"] = 0, ["run"] = 0 }, "the next run replaces it");
        RetentionMetrics.RunFreshFor.Should().Be(TimeSpan.FromHours(26));
        FluentActions.Invoking(() => metrics.RunFinished(null, RetentionMetrics.RunFreshFor)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RetentionMetrics_Should_ReportThePolicyJobsByKindForTheirWindow_When_Checked()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new RetentionMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var kinds = new List<(string Kind, int Failed)> { (TimescalePolicyJobs.Compression, 0), (TimescalePolicyJobs.Retention, 2) };

        metrics.PolicyJobs(kinds, TimescaleJobs.ReportFreshFor);
        kinds.Add((TimescalePolicyJobs.Refresh, 5));
        clock.Advance(TimeSpan.FromMinutes(25));

        reader.Observe(RetentionMetrics.PolicyJobsFailedName).ToDictionary(m => m.Tags["kind"], m => m.Value, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["compression"] = 0, ["retention"] = 2 }, "its own copy of the check");
        clock.Advance(TimeSpan.FromSeconds(1));
        reader.Observe(RetentionMetrics.PolicyJobsFailedName).Should().BeEmpty("a check that stopped reads as missing");
        FluentActions.Invoking(() => metrics.PolicyJobs(null, TimescaleJobs.ReportFreshFor)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task PurgeAsync_Should_ReportARunThatFailedAsAWhole_When_TheDatabaseIsOutOfReach()
    {
        using var metrics = new RetentionMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        // Nothing listens on the port: the run fails before any data set.
        var round = new RetentionRound(new DatabaseSettings { Host = "127.0.0.1", Port = 1 }, new RetentionJobSettings(), NullLogger<RetentionRound>.Instance, metrics);
        var job = new RetentionJobs(round, new RetentionJobSettings(), NullLogger<RetentionJobs>.Instance, metrics: metrics);

        var purge = () => job.PurgeAsync(new TickerFunctionContext(), TestContext.Current.CancellationToken);

        await purge.Should().ThrowAsync<Exception>("TickerQ records the failure as before");
        reader.Observe(RetentionMetrics.ProblemsName).Should().ContainSingle()
            .Which.Should().Match<(double Value, IReadOnlyDictionary<string, string> Tags)>(m => m.Value == 1 && m.Tags["outcome"] == "run");
    }

    [Fact]
    public async Task PurgeAsync_Should_ReportARunThatFailedAsAWhole_When_ItRunsOutItsRunTimeout()
    {
        // The database accepts the connection and never answers: the run ends at its run timeout, which counts as a failed run.
        await using var silent = new SilentServer();
        using var metrics = new RetentionMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var round = new RetentionRound(new DatabaseSettings { Host = "127.0.0.1", Port = silent.Port, ConnectTimeoutSeconds = 2 }, new RetentionJobSettings(),
            NullLogger<RetentionRound>.Instance, metrics);
        var job = new RetentionJobs(round, new RetentionJobSettings(), NullLogger<RetentionJobs>.Instance, metrics: metrics) { RunTimeout = TimeSpan.FromSeconds(1) };

        var purge = () => job.PurgeAsync(new TickerFunctionContext(), TestContext.Current.CancellationToken);

        await purge.Should().ThrowAsync<Ariva.Infra.Resilience.RunTimeoutException>();
        reader.Observe(RetentionMetrics.ProblemsName).Should().ContainSingle()
            .Which.Should().Match<(double Value, IReadOnlyDictionary<string, string> Tags)>(m => m.Value == 1 && m.Tags["outcome"] == "run");
    }

    [Fact]
    public async Task PurgeAsync_Should_ReportNothing_When_TheHostStopsTheRun()
    {
        using var metrics = new RetentionMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var round = new RetentionRound(new DatabaseSettings { Host = "127.0.0.1", Port = 1 }, new RetentionJobSettings(), NullLogger<RetentionRound>.Instance, metrics);
        var job = new RetentionJobs(round, new RetentionJobSettings(), NullLogger<RetentionJobs>.Instance, metrics: metrics);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var purge = () => job.PurgeAsync(new TickerFunctionContext(), stopping.Token);

        await purge.Should().ThrowAsync<OperationCanceledException>();
        reader.Observe(RetentionMetrics.ProblemsName).Should().BeEmpty("a stop is not a failed run");
    }

    [Fact]
    public void PolicyJobCheck_Should_RunEveryTenMinutesWithinAMinute_When_Scheduled()
    {
        var function = typeof(TimescaleJobs).GetMethod(nameof(TimescaleJobs.CheckAsync)).CustomAttributes
            .Single(a => a.AttributeType == typeof(TickerFunctionAttribute));

        function.ConstructorArguments.Select(a => a.Value).Should().StartWith([TimescaleJobs.PolicyJobs, "0 */10 * * * *"]);
        new TimescaleJobs(new TimescalePolicyJobs(new DatabaseSettings()), new RetentionMetrics(), NullLogger<TimescaleJobs>.Instance).RunTimeout.Should().Be(TimeSpan.FromMinutes(1));
        TimescaleJobs.ReportFreshFor.Should().Be(TimeSpan.FromMinutes(25), "two checks may be missed before the copy lapses");
    }

    [Fact]
    public void PolicyJobs_Should_ReadAtMostMaxProceduresRows_When_ItsStatementRuns()
    {
        TimescalePolicyJobs.Failed.TrimEnd().Should().EndWith($"LIMIT {TimescalePolicyJobs.MaxProcedures}");
        TimescalePolicyJobs.MaxProcedures.Should().Be(20);
    }

    [Fact]
    public async Task PolicyJobCheck_Should_ReportNothing_When_TheDatabaseIsOutOfReach()
    {
        using var metrics = new RetentionMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var check = new TimescaleJobs(new TimescalePolicyJobs(new DatabaseSettings { Host = "127.0.0.1", Port = 1 }), metrics, NullLogger<TimescaleJobs>.Instance);

        var run = () => check.CheckAsync(new TickerFunctionContext(), TestContext.Current.CancellationToken);

        await run.Should().ThrowAsync<Exception>();
        reader.Observe(RetentionMetrics.PolicyJobsFailedName).Should().BeEmpty("the database's own rule reports it");
    }

    [Fact]
    public async Task Cronz_Should_ResolveThePolicyJobCheck_When_ItRuns()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Cronz);
        await using var scope = app.Services.CreateAsyncScope();

        ActivatorUtilities.CreateInstance<TimescaleJobs>(scope.ServiceProvider).Should().NotBeNull();
        app.Services.GetRequiredService<RetentionMetrics>().Should().BeSameAs(app.Services.GetRequiredService<RetentionMetrics>());
    }
}
