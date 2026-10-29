using Ariva.Core.Services;
using Ariva.Core.Services.Displays;
using Ariva.Infra.Live;
using Ariva.Infra.Services.Displays;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-259d: the display stale gauge, the board's stale rule the sweep shares with the player, and the monitor that runs the sweep
/// in Ariva.Api.Main (the sweep itself, its lock and its read of Redis are in the integration tests, <c>DisplayStaleTests</c>).
/// </summary>
public sealed class DisplayStaleTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LiveZoneSnapshot Published(double secondsAgo) =>
        new("DMO/A-CIT", Now.AddMinutes(-1), 12, true, false, 7.5, 3.0, null, false, Now.AddSeconds(-secondsAgo));

    [Fact]
    public void Stale_Should_ReportEachSiteForItsWindow_When_ASweepHasJudgedThem()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new DisplayMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe(DisplayMetrics.StaleName).Should().BeEmpty("nothing before the first sweep");

        metrics.Stale([new DisplayStaleSite("DMO", 12, 3), new DisplayStaleSite("AUH-TA", 4, 0)], TimeSpan.FromMinutes(2));
        clock.Advance(TimeSpan.FromMinutes(2));

        reader.Observe(DisplayMetrics.StaleName).Select(m => (m.Tags["site"], m.Value)).Should().BeEquivalentTo([("DMO", 3d), ("AUH-TA", 0d)],
            "every site the sweep judged, 0 when none of its displays is stale");
        reader.Observe(DisplayMetrics.StaleName).Should().AllSatisfy(m => m.Tags.Keys.Should().Equal("site"));

        clock.Advance(TimeSpan.FromSeconds(1));
        reader.Observe(DisplayMetrics.StaleName).Should().BeEmpty("a replica that stopped sweeping (or lost the lock) reads as missing");

        metrics.Stale([new DisplayStaleSite("DMO", 12, 1)], TimeSpan.FromMinutes(2));
        reader.Observe(DisplayMetrics.StaleName).Should().ContainSingle().Which.Value.Should().Be(1, "the last sweep replaces the one before");
        metrics.Clear();
        reader.Observe(DisplayMetrics.StaleName).Should().BeEmpty("the sweep stopped");
        FluentActions.Invoking(() => metrics.Stale(null, TimeSpan.FromMinutes(2))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Stale_Should_ReportAtMostItsSitesTheMostStaleFirst_When_ASweepJudgedMore()
    {
        using var metrics = new DisplayMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var sites = Enumerable.Range(0, DisplayMetrics.MaxSites + 20).Select(i => new DisplayStaleSite($"S{i:000}", 5, i % 7 == 0 ? 2 : 0)).ToList();
        sites.Add(new DisplayStaleSite("ZZZ", 5, 5));

        metrics.Stale(sites, TimeSpan.FromMinutes(2));
        sites.Clear();

        var reported = reader.Observe(DisplayMetrics.StaleName).Select(m => (Site: m.Tags["site"], m.Value)).ToList();
        reported.Should().HaveCount(DisplayMetrics.MaxSites, "the copy is the metrics' own, and bounded");
        reported.Should().Contain(("ZZZ", 5d), "the most stale site first, whatever its code");
        reported.Count(r => r.Value == 2).Should().Be(Enumerable.Range(0, DisplayMetrics.MaxSites + 20).Count(i => i % 7 == 0), "every site with stale displays");
        reported.Where(r => r.Value == 0).Select(r => r.Site).Should().BeInAscendingOrder(StringComparer.Ordinal).And.NotContain("S118", "then by code: the last ones without stale displays are left out");
    }

    [Theory]
    [InlineData(0, 60, false)]
    [InlineData(59.9, 60, false)]
    [InlineData(60, 60, false)]
    [InlineData(60.04, 60, false)]
    [InlineData(60.06, 60, true)]
    [InlineData(1_800.1, 1_800, true)]
    [InlineData(-5, 60, false)]
    public void IsStale_Should_FollowThePlayersRule_When_TheBoardGivesItsAge(double secondsAgo, int staleSeconds, bool stale)
    {
        // The player shows a band while the board's age (to a tenth of a second, never negative) is at most the threshold.
        SvcDisplayBoard.IsStale(Published(secondsAgo), Now, staleSeconds).Should().Be(stale);
        SvcDisplayBoard.AgeSeconds(Published(secondsAgo), Now).Should().Be(Math.Max(0, Math.Round(secondsAgo, 1)));
    }

    [Fact]
    public void IsStale_Should_BeTrue_When_TheZoneHasNoSnapshot()
    {
        SvcDisplayBoard.IsStale(null, Now, 1_800).Should().BeTrue("the board carries no age and the player shows the neutral message");
    }

    [Fact]
    public void Monitor_Should_SweepAndEndASweepWithinAMinute_When_ItRuns()
    {
        using var metrics = new DisplayMetrics();
        var monitor = Monitor(new FakeSweeps(), metrics, new Logged()).Monitor;
        monitor.RunTimeout.Should().Be(TimeSpan.FromMinutes(1));
        monitor.Interval.Should().Be(TimeSpan.FromMinutes(1), "the shortest stale threshold a display takes");
        SvcDisplayStaleness.LockClass.Should().Be(54);
        new[] { 25, 26, 36, 37, 38, 41, 42, 49, 50, 51, 52, 53, 141, 145, 1421, 1431, 1432, 1441 }
            .Should().NotContain(SvcDisplayStaleness.LockClass, "the two-key classes other locks take");
        SvcDisplayStaleness.MaxDisplays.Should().Be(2_000);
    }

    [Fact]
    public async Task Monitor_Should_KeepItsLastCopyUntilItLapses_When_AnotherReplicaSweepsTheNextRound()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new DisplayMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var sweeps = new FakeSweeps(Ran(new DisplayStaleSite("DMO", 4, 2)), DisplayStaleSweep.Skipped);
        var (monitor, unitOfWork) = Monitor(sweeps, metrics, new Logged(), clock);

        await monitor.RunOnceAsync(Ct);
        await monitor.RunOnceAsync(Ct);

        monitor.Last.Should().Be(DisplayStaleSweep.Skipped);
        reader.Observe(DisplayMetrics.StaleName).Should().ContainSingle().Which.Value.Should().Be(2, "the skipped round leaves this replica's copy as it was");
        clock.Advance(2 * monitor.Interval + TimeSpan.FromSeconds(1));
        reader.Observe(DisplayMetrics.StaleName).Should().BeEmpty("two intervals after the last sweep it ran");
        unitOfWork.Verify(u => u.EndAsync(It.IsAny<CancellationToken>()), Times.Exactly(2), "each sweep's transaction ends, and with it the lock");
    }

    [Fact]
    public async Task Monitor_Should_WarnOncePerSpell_When_MoreDisplaysAreEnabledThanASweepJudges()
    {
        var log = new Logged();
        var sweeps = new FakeSweeps(Ran(true), Ran(true), Ran(false), Ran(true));
        var (monitor, _) = Monitor(sweeps, new DisplayMetrics(), log);

        for (var i = 0; i < 4; i++)
            await monitor.RunOnceAsync(Ct);

        log.Warnings.Should().HaveCount(2, "once when the bound is first passed, and again after it was not")
            .And.AllSatisfy(w => w.Should().Contain("2000"));
    }

    [Fact]
    public async Task Monitor_Should_RollBackAndReportNothingNew_When_ASweepFails()
    {
        using var metrics = new DisplayMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var sweeps = new FakeSweeps(Ran(new DisplayStaleSite("DMO", 4, 1)));
        var (monitor, unitOfWork) = Monitor(sweeps, metrics, new Logged());
        await monitor.RunOnceAsync(Ct);
        sweeps.Fail = new InvalidOperationException("the database restarted");

        await FluentActions.Awaiting(() => monitor.RunOnceAsync(Ct)).Should().ThrowAsync<InvalidOperationException>();

        unitOfWork.Verify(u => u.RollbackAsync(CancellationToken.None), Times.Once());
        reader.Observe(DisplayMetrics.StaleName).Should().ContainSingle().Which.Value.Should().Be(1, "the copy of the sweep before, until it lapses");
    }

    [Fact]
    public async Task Monitor_Should_ReportNothing_When_ItStops()
    {
        // Every sweep fails (the database restarting); stopping ends the copy reported before. The stop waits for the first
        // sweep's error: the host may start ExecuteAsync on the thread pool, and a stop before it began ends a loop that never ran.
        using var metrics = new DisplayMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var log = new Logged();
        var (monitor, _) = Monitor(new FakeSweeps { Fail = new InvalidOperationException("the database restarted") }, metrics, log);
        metrics.Stale([new DisplayStaleSite("DMO", 4, 2)], TimeSpan.FromMinutes(2));

        await monitor.StartAsync(Ct);
        await log.FirstError.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        reader.Observe(DisplayMetrics.StaleName).Should().ContainSingle("a failed sweep changes nothing");
        await monitor.StopAsync(Ct);

        reader.Observe(DisplayMetrics.StaleName).Should().BeEmpty();
        monitor.Dispose();
    }

    [Fact]
    public void AddArivaDisplayStaleMonitor_Should_RegisterTheSweepItsServiceAndItsGauge_When_MainAddsIt()
    {
        var services = new ServiceCollection();
        Ariva.Di.Extensions.DisplayStaleExtensions.AddArivaDisplayStaleMonitor(services);

        services.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DisplayStaleMonitor));
        services.Should().Contain(d => d.ServiceType == typeof(ISvcDisplayStaleness) && d.ImplementationType == typeof(SvcDisplayStaleness) &&
                                       d.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(d => d.ServiceType == typeof(DisplayMetrics) && d.Lifetime == ServiceLifetime.Singleton);
    }

    private static DisplayStaleSweep Ran(params DisplayStaleSite[] sites) => new(true, sites, false, false);

    private static DisplayStaleSweep Ran(bool truncated) => new(true, [new DisplayStaleSite("DMO", 2_000, 0)], truncated, false);

    private static (DisplayStaleMonitor Monitor, Mock<IUnitOfWork> UnitOfWork) Monitor(FakeSweeps sweeps, DisplayMetrics metrics, Logged log, TimeProvider clock = null)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped<ISvcDisplayStaleness>(_ => sweeps);
        var provider = services.BuildServiceProvider();
        return (new DisplayStaleMonitor(provider.GetRequiredService<IServiceScopeFactory>(), metrics, clock ?? TimeProvider.System, log), unitOfWork);
    }

    /// <summary>The sweeps a test gives, in order (the last repeated), or a failure.</summary>
    private sealed class FakeSweeps(params DisplayStaleSweep[] sweeps) : ISvcDisplayStaleness
    {
        private int _next;

        public Exception Fail { get; set; }

        public Task<DisplayStaleSweep> SweepAsync(CancellationToken ct = default)
        {
            if (Fail is not null)
                return Task.FromException<DisplayStaleSweep>(Fail);
            var sweep = sweeps.Length == 0 ? Ran() : sweeps[Math.Min(_next, sweeps.Length - 1)];
            _next++;
            return Task.FromResult(sweep);
        }
    }

    /// <summary>The monitor's warnings, and the first error (a failed sweep).</summary>
    private sealed class Logged : ILogger<DisplayStaleMonitor>
    {
        public TaskCompletionSource FirstError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
            if (logLevel == LogLevel.Error)
                FirstError.TrySetResult();
        }
    }
}
