using Ariva.Api.Cronz.Jobs;
using Ariva.Core.Staffing;
using Ariva.Di.Extensions;
using Ariva.Infra.Staffing;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;

namespace Ariva.UnitTests.Jobs;

/// <summary>
/// ARV-181a: the staffing recommendation job in Ariva.Api.Cronz. It runs every minute at second 45 (after the queue
/// minute's live part); the runner reads the database only (no HTTP client of any kind can reach it); its settings are
/// checked at start, and the Cronz host resolves everything a run takes.
/// </summary>
public sealed class StaffingJobsTests
{
    [Fact]
    public void Runner_Should_TakeNoHttpClient_When_Constructed()
    {
        foreach (var type in new[] { typeof(StaffingRecommendations), typeof(StaffingInputs), typeof(StaffingJobs) })
            type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Should().NotContain(t => typeof(HttpClient).IsAssignableFrom(t) || t == typeof(IHttpClientFactory) || t.Namespace == "System.Net.Http", type.Name);
    }

    [Fact]
    public void Job_Should_RunEveryMinuteAtSecond45_When_Scheduled()
    {
        var function = typeof(StaffingJobs).GetMethod(nameof(StaffingJobs.RecommendAsync)).CustomAttributes
            .Single(a => a.AttributeType == typeof(TickerFunctionAttribute));

        function.ConstructorArguments.Select(a => a.Value).Should().StartWith([StaffingJobs.Recommendations, "45 * * * * *"]);
        StaffingRecommendations.LockClass.Should().Be(51, "the class no other advisory lock takes (25, 26, 37, 38, 41, 42, 49, 50)");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(59_999, false)]
    [InlineData(60_000, true)]
    [InlineData(95_000, true)]
    public void Overran_Should_BeTrueFromAWholeMinute_When_ARunTookThatLong(int milliseconds, bool overran)
    {
        // ARV-124a: TickerQ starts the next occurrence beside a run still going, so a run of a minute or more is a warning.
        StaffingJobs.Overran(new StaffingRun(1, 0, 0, 0, 1, 13) { Elapsed = TimeSpan.FromMilliseconds(milliseconds) }).Should().Be(overran);
        StaffingJobs.Interval.Should().Be(TimeSpan.FromMinutes(1), "the job runs every minute at second 45");
        StaffingJobs.Overran(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0, 0, 1_000, LogLevel.Debug)]
    [InlineData(1, 0, 0, 1_000, LogLevel.Warning)]
    [InlineData(0, 1, 0, 1_000, LogLevel.Warning)]
    [InlineData(0, 0, 1, 1_000, LogLevel.Warning)]
    [InlineData(0, 0, 0, 59_999, LogLevel.Debug)]
    [InlineData(0, 0, 0, 60_000, LogLevel.Warning)]
    public void LevelOf_Should_WarnOnATroubledOrOverlongRun_When_TheRunEnds(int failed, int refused, int lanesRefused, int milliseconds, LogLevel level)
    {
        var run = new StaffingRun(3, 0, failed, refused, 5, 65, lanesRefused) { Elapsed = TimeSpan.FromMilliseconds(milliseconds) };

        StaffingJobs.LevelOf(run).Should().Be(level);
        StaffingJobs.Troubled(run).Should().Be(failed + refused + lanesRefused > 0);
    }

    [Theory]
    [InlineData("Staffing:BusyCap", "0.4")]
    [InlineData("Staffing:TargetMinutes", "0")]
    [InlineData("Staffing:Sites:dmo:BusyCap", "0.9")]
    [InlineData("Staffing:Sites:DMO:FallbackCycleSeconds:VIS", "2")]
    public void AddArivaStaffingJob_Should_RefuseTheHost_When_TheSettingsAreOutOfRange(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { [key] = value }).Build();

        FluentActions.Invoking(() => new ServiceCollection().AddArivaStaffingJob(configuration))
            .Should().Throw<InvalidOperationException>().WithMessage("*Staffing*");
        FluentActions.Invoking(() => new ServiceCollection().AddArivaStaffingRecommendations(configuration))
            .Should().Throw<InvalidOperationException>().WithMessage("*Staffing*");
    }

    [Fact]
    public void AddArivaStaffingJob_Should_BindTheSection_When_Valid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Staffing:Sites:DMO:LaneTargetMinutes:VIS"] = "12",
            ["Staffing:Sites:DMO:FallbackCycleSeconds:VIS"] = "95"
        }).Build();
        var services = new ServiceCollection().AddArivaStaffingJob(configuration);

        var settings = services.Single(d => d.ServiceType == typeof(StaffingSettings)).ImplementationInstance.Should().BeOfType<StaffingSettings>().Subject;
        settings.TargetFor("DMO", "VIS").Should().Be(12);
        settings.FallbackCycleSecondsFor("DMO", "VIS").Should().Be(95);
        services.Should().Contain(d => d.ServiceType == typeof(StaffingRecommendations));
        services.Should().Contain(d => d.ServiceType == typeof(StaffingInputs) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task Cronz_Should_ResolveWhatTheStaffingJobRunsWith_When_ItRuns()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Cronz);
        await using var scope = app.Services.CreateAsyncScope();

        app.Services.GetService<StaffingRecommendations>().Should().NotBeNull();
        scope.ServiceProvider.GetService<StaffingInputs>().Should().NotBeNull("resolved from a scope per site at every run");
        ActivatorUtilities.CreateInstance<StaffingJobs>(scope.ServiceProvider).Should().NotBeNull();
    }
}
