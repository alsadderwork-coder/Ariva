using Ariva.Core.Queueing;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-259b: the bin maturity sweep's gauge, settings and registration. A queue bin is mature once its end, the censoring
/// timeout and the longest lateness of any site have passed; <c>ariva.stream.bins_overdue</c> is the bins whose latest revision
/// is still Provisional past that, by site, reported only by the replica that swept lately (the count itself is in the
/// integration tests, <c>BinMaturityTests</c>).
/// </summary>
public sealed class BinMaturityTests
{
    private static StreamSettings Bind(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))).Build()
            .GetSection(StreamSettings.SectionName).Get<StreamSettings>() ?? new StreamSettings();

    [Fact]
    public void BinsOverdue_Should_ReportEachSiteForItsWindow_When_ASweepHasCounted()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new StreamMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe(StreamMetrics.BinsOverdueName).Should().BeEmpty("nothing before the first sweep");

        metrics.OverdueBins([("DMO", 3), ("AUH-TA", 1)], TimeSpan.FromSeconds(120));
        clock.Advance(TimeSpan.FromSeconds(120));

        reader.Observe(StreamMetrics.BinsOverdueName).Select(m => (m.Tags["site"], m.Value))
            .Should().BeEquivalentTo([("DMO", 3d), ("AUH-TA", 1d)]);
        reader.Observe(StreamMetrics.BinsOverdueName).Should().AllSatisfy(m => m.Tags.Keys.Should().Equal("site"));

        clock.Advance(TimeSpan.FromSeconds(1));
        reader.Observe(StreamMetrics.BinsOverdueName).Should().BeEmpty("a replica that stopped sweeping (or lost the lock) reads as missing");

        metrics.OverdueBins([], TimeSpan.FromSeconds(120));
        reader.Observe(StreamMetrics.BinsOverdueName).Should().BeEmpty("a sweep that found none reports no site");
        metrics.OverdueBins([("DMO", 2)], TimeSpan.FromSeconds(120));
        reader.Observe(StreamMetrics.BinsOverdueName).Should().ContainSingle().Which.Value.Should().Be(2, "the last sweep replaces the one before");
        metrics.Clear();
        reader.Observe(StreamMetrics.BinsOverdueName).Should().ContainSingle("a queue worker that stops leaves the sweep's copy alone");
        metrics.ClearOverdueBins();
        reader.Observe(StreamMetrics.BinsOverdueName).Should().BeEmpty("the sweep stopped");
        FluentActions.Invoking(() => metrics.OverdueBins(null, TimeSpan.FromSeconds(120))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BinsOverdue_Should_KeepItsOwnCopy_When_TheCallerChangesItsList()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new StreamMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var sites = new List<(string Site, int Count)> { ("DMO", 3) };

        metrics.OverdueBins(sites, TimeSpan.FromSeconds(120));
        sites.Add(("DXB", 9));

        reader.Observe(StreamMetrics.BinsOverdueName).Should().ContainSingle().Which.Tags["site"].Should().Be("DMO");
    }

    [Fact]
    public void Defaults_Should_SweepEveryMinuteOverADay_When_NothingIsConfigured()
    {
        var settings = new StreamSettings();

        (settings.BinMaturitySweepSeconds, settings.BinMaturityLookbackHours).Should().Be((60, 24));
        settings.MaxLatenessSeconds.Should().Be(90, "the default lateness when no site has its own");
        settings.Problems().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Stream:BinMaturitySweepSeconds", "9", "Stream:BinMaturitySweepSeconds is from 10 to 3,600.")]
    [InlineData("Stream:BinMaturitySweepSeconds", "3601", "Stream:BinMaturitySweepSeconds is from 10 to 3,600.")]
    [InlineData("Stream:BinMaturityLookbackHours", "0", "Stream:BinMaturityLookbackHours is from 1 to 168.")]
    [InlineData("Stream:BinMaturityLookbackHours", "169", "Stream:BinMaturityLookbackHours is from 1 to 168.")]
    public void Problems_Should_RefuseTheSweep_When_ItIsOutOfRange(string key, string value, string problem)
    {
        Bind((key, value)).Problems().Should().ContainSingle().Which.Should().Be(problem);
    }

    [Theory]
    [InlineData("10", "1")]
    [InlineData("3600", "168")]
    public void Problems_Should_AcceptTheSweepsEdges_When_TheyAreInRange(string seconds, string hours)
    {
        Bind(("Stream:BinMaturitySweepSeconds", seconds), ("Stream:BinMaturityLookbackHours", hours)).Problems().Should().BeEmpty();
    }

    [Fact]
    public void MaxLatenessSeconds_Should_TakeTheLongestOfTheDefaultAndEverySite_When_SitesHaveTheirOwn()
    {
        Bind(("Stream:LatenessSeconds", "120"), ("Stream:Sites:DMO:LatenessSeconds", "300"), ("Stream:Sites:DXB:LatenessSeconds", "60"))
            .MaxLatenessSeconds.Should().Be(300, "a site's own lateness above the default");
        Bind(("Stream:LatenessSeconds", "120"), ("Stream:Sites:DXB:LatenessSeconds", "60"))
            .MaxLatenessSeconds.Should().Be(120, "a site's own lateness below the default");
        Bind(("Stream:LatenessSeconds", "120"), ("Stream:Sites:DMO:PushIntervalSeconds", "30"))
            .MaxLatenessSeconds.Should().Be(120, "a site that sets only its push interval runs under the default lateness");
    }

    [Fact]
    public void MaturityDelay_Should_BeTheCensoringTimeoutAndTheLongestLateness_When_TheEngineRunsItsSettings()
    {
        var settings = Bind(("Stream:Enabled", "true"), ("Stream:Sites:DMO:LatenessSeconds", "300"));

        Monitor(settings).MaturityDelay.Should().Be(TimeSpan.FromMinutes(120) + TimeSpan.FromSeconds(300), "the engine's default censoring timeout (F5, F6)");
        Monitor(settings, new ZoneProcessorSettings { Engine = new QueueEngineSettings { CensorAfter = TimeSpan.FromMinutes(45) } })
            .MaturityDelay.Should().Be(TimeSpan.FromMinutes(45) + TimeSpan.FromSeconds(300), "the engine's own settings when they are given");
        new QueueEngineSettings().CensorAfter.Should().Be(TimeSpan.FromMinutes(120));
    }

    [Fact]
    public void Monitor_Should_EndASweepWithinAMinute_When_ItRuns()
    {
        Monitor(new StreamSettings()).RunTimeout.Should().Be(TimeSpan.FromMinutes(1));
        StreamStore.BinMaturityLockClass.Should().Be(53);
        new[] { 25, 26, 36, 37, 38, 41, 42, 49, 50, 51, 52, 141, 145, 1421, 1431, 1432, 1441 }
            .Should().NotContain(StreamStore.BinMaturityLockClass, "the two-key classes other locks take");
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task CountOverdueBins_Should_RefuseATime_When_ItIsNotUtc(DateTimeKind kind)
    {
        // Refused before any connection: the store's database is never reached.
        var store = new StreamStore(new DatabaseSettings { Host = "127.0.0.1", Port = 9 }, TimeProvider.System, NullLogger<StreamStore>.Instance);
        var utc = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc);
        var other = DateTime.SpecifyKind(utc, kind);

        await FluentActions.Awaiting(() => store.CountOverdueBinsAsync(other, utc, TestContext.Current.CancellationToken)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => store.CountOverdueBinsAsync(utc.AddHours(-24), other, TestContext.Current.CancellationToken)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Monitor_Should_ReportNothing_When_ItStops()
    {
        // The database refuses at once (port 9): the sweep fails, logs and waits; stopping ends the copy it last reported.
        using var metrics = new StreamMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var monitor = new BinMaturityMonitor(new StreamStore(new DatabaseSettings { Host = "127.0.0.1", Port = 9 }, TimeProvider.System, NullLogger<StreamStore>.Instance),
            new StreamSettings { Enabled = true }, metrics, TimeProvider.System, NullLogger<BinMaturityMonitor>.Instance);
        metrics.OverdueBins([("DMO", 2)], TimeSpan.FromMinutes(2));

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        reader.Observe(StreamMetrics.BinsOverdueName).Should().ContainSingle("a failed sweep changes nothing");
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        reader.Observe(StreamMetrics.BinsOverdueName).Should().BeEmpty();
        monitor.Dispose();
    }

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, false, false)]
    public void AddArivaQueueStream_Should_RegisterTheSweep_When_TheStreamIsEnabledWithOrWithoutKafka(bool stream, bool kafka, bool sweep, bool worker)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([
            new KeyValuePair<string, string>("Stream:Enabled", stream.ToString()),
            new KeyValuePair<string, string>("Kafka:Enabled", kafka.ToString())
        ]).Build();
        var services = new ServiceCollection();

        Ariva.Di.Extensions.StreamExtensions.AddArivaQueueStream(services, configuration);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
        hosted.Contains(typeof(BinMaturityMonitor)).Should().Be(sweep, "the sweep reads the stored bins and needs no Kafka");
        hosted.Contains(typeof(QueueStreamWorker)).Should().Be(worker);
    }

    private static BinMaturityMonitor Monitor(StreamSettings settings, ZoneProcessorSettings zoneSettings = null) =>
        new(new StreamStore(new DatabaseSettings(), TimeProvider.System, NullLogger<StreamStore>.Instance), settings, new StreamMetrics(),
            TimeProvider.System, NullLogger<BinMaturityMonitor>.Instance, zoneSettings);
}
