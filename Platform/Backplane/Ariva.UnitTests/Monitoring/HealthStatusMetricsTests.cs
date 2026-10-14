using Ariva.Di.Extensions;
using Ariva.Infra.Monitoring;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-125a: <c>ariva.health.status</c> reports each readiness check (0 Healthy, 1 Degraded, 2 Unhealthy) from the host's
/// health check publisher, only the checks tagged <c>ready</c>, and nothing once the last report is stale; the check's
/// description and exception never reach the metric.
/// </summary>
public sealed class HealthStatusMetricsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HealthReport Report(params (string Name, HealthStatus Status)[] checks) => new(
        checks.ToDictionary(c => c.Name, c => new HealthReportEntry(c.Status, "detail naming db-host-1", TimeSpan.Zero, new InvalidOperationException("secret"), null)),
        TimeSpan.Zero);

    [Theory]
    [InlineData(HealthStatus.Healthy, 0)]
    [InlineData(HealthStatus.Degraded, 1)]
    [InlineData(HealthStatus.Unhealthy, 2)]
    public void ValueOf_Should_NumberTheStatus_When_ACheckReports(HealthStatus status, int value) =>
        HealthStatusMetrics.ValueOf(status).Should().Be(value);

    [Fact]
    public async Task Gauge_Should_ReportEachCheckByName_When_ThePublisherHasRun()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new HealthStatusMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe(HealthStatusMetrics.StatusName).Should().BeEmpty("nothing before the first report");

        await metrics.PublishAsync(Report(("database", HealthStatus.Healthy), ("kafka", HealthStatus.Degraded), ("masstransit-bus", HealthStatus.Degraded),
            ("redis", HealthStatus.Unhealthy)), Ct);

        var measured = reader.Observe(HealthStatusMetrics.StatusName);
        measured.Select(m => (m.Tags["check"], m.Value)).Should().BeEquivalentTo([("database", 0d), ("kafka", 1d), ("masstransit-bus", 1d), ("redis", 2d)]);
        measured.Should().OnlyContain(m => m.Tags.Count == 1, "the check's name only: no description, exception or data");
    }

    [Fact]
    public async Task Gauge_Should_ReportNothing_When_TheLastReportIsStale()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new HealthStatusMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        await metrics.PublishAsync(Report(("database", HealthStatus.Healthy)), Ct);

        clock.Advance(HealthStatusMetrics.FreshFor);
        reader.Observe(HealthStatusMetrics.StatusName).Should().ContainSingle("still within its window");
        clock.Advance(TimeSpan.FromSeconds(1));
        reader.Observe(HealthStatusMetrics.StatusName).Should().BeEmpty("a publisher that stopped reads as missing, not as its last state");
    }

    [Fact]
    public void AddArivaMonitoring_Should_PublishTheReadyChecksEveryFifteenSeconds_When_AHostRegistersIt()
    {
        var services = new ServiceCollection().AddMetrics().AddLogging();
        services.AddHealthChecks();
        services.AddArivaMonitoring();
        services.AddArivaMonitoring();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHealthCheckPublisher>().Should().ContainSingle().Which.Should().BeSameAs(provider.GetRequiredService<HealthStatusMetrics>());
        var options = provider.GetRequiredService<IOptions<HealthCheckPublisherOptions>>().Value;
        options.Period.Should().Be(TimeSpan.FromSeconds(15));
        options.Predicate(new HealthCheckRegistration("kafka", _ => null, HealthStatus.Degraded, [HealthExtensions.ReadyTag])).Should().BeTrue();
        options.Predicate(new HealthCheckRegistration("other", _ => null, HealthStatus.Degraded, ["live"])).Should().BeFalse("only the readiness checks");
        provider.GetServices<IHostedService>().OfType<CertificateMetrics>().Should().ContainSingle("the certificate gauge exists from the start");
    }

    [Fact]
    public async Task Publisher_Should_FeedTheGaugeFromTheReadyChecks_When_TheHostRuns()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHealthChecks()
            .AddCheck("database", () => HealthCheckResult.Healthy(), [HealthExtensions.ReadyTag])
            .AddCheck("masstransit-bus", () => HealthCheckResult.Degraded("bus endpoint faulted"), [HealthExtensions.ReadyTag])
            .AddCheck("not-ready", () => HealthCheckResult.Unhealthy(), ["live"]);
        builder.Services.AddArivaMonitoring();
        builder.Services.PostConfigure<HealthCheckPublisherOptions>(options =>
        {
            options.Delay = TimeSpan.Zero;
            options.Period = TimeSpan.FromSeconds(1);
        });
        using var host = builder.Build();
        using var reader = new InstrumentReader(host.Services.GetRequiredService<HealthStatusMetrics>().Meter);
        await host.StartAsync(Ct);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            IReadOnlyList<(double Value, IReadOnlyDictionary<string, string> Tags)> measured;
            while ((measured = reader.Observe(HealthStatusMetrics.StatusName)).Count == 0 && DateTime.UtcNow < until)
                await Task.Delay(100, Ct);

            measured.Select(m => (m.Tags["check"], m.Value)).Should().BeEquivalentTo([("database", 0d), ("masstransit-bus", 1d)],
                "a Degraded bus reads 1, and a check not tagged ready is not reported");
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }
}
