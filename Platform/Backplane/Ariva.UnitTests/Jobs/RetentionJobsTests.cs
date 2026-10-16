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
}
