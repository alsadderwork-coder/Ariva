using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ariva.Infra.Monitoring;

/// <summary>
/// The host's readiness checks as a metric (meter <c>Ariva.Health</c>, ARV-125a): <see cref="StatusName"/>, one value per
/// <c>check</c> (database, redis, kafka, masstransit-bus: the checks tagged <c>ready</c>), 0 Healthy, 1 Degraded,
/// 2 Unhealthy. It is the health check publisher of the host, so the checks run on the publisher's own schedule (every
/// <see cref="Period"/>), not only when the kubelet asks, and a Degraded check, which leaves readiness at 200 by the owner's
/// ARV-125 decision, still raises an alert. The readiness endpoint itself names no check (CWE-200); this gauge is only
/// exported to the site's collector. A report older than <see cref="FreshFor"/> is not reported, so a publisher that
/// stopped reads as missing rather than as its last state. The check name is the registration's (code, never a request)
/// and is reduced to <see cref="OtherCheck"/> when it is not a plain lower-case name.
/// </summary>
public sealed partial class HealthStatusMetrics : IHealthCheckPublisher, IDisposable
{
    public const string MeterName = "Ariva.Health";
    public const string StatusName = "ariva.health.status";

    /// <summary>The tag value of a check whose name is not a plain lower-case name.</summary>
    public const string OtherCheck = "other";

    /// <summary>How often the publisher runs the readiness checks.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    /// <summary>How long a report is reported for: four periods.</summary>
    public static readonly TimeSpan FreshFor = Period * 4;

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private volatile Snapshot _last;

    #endregion

    private sealed record Snapshot(IReadOnlyList<(string Check, int Status)> Checks, DateTimeOffset At);

    #region Constructors

    public HealthStatusMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(StatusName, Observe,
            description: "Readiness checks of the host: 0 Healthy, 1 Degraded, 2 Unhealthy, per check");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>The value of a health status: 0 Healthy, 1 Degraded, 2 Unhealthy.</summary>
    public static int ValueOf(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 0,
        HealthStatus.Degraded => 1,
        _ => 2
    };

    /// <summary>A check name as a tag value: the registration's name when it is a plain lower-case name, else <see cref="OtherCheck"/>.</summary>
    public static string CheckTag(string name) => name is { Length: <= 64 } && CheckRule().IsMatch(name) ? name : OtherCheck;

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        _last = new Snapshot([.. report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (CheckTag(e.Key), ValueOf(e.Value.Status)))], _time.GetUtcNow());
        return Task.CompletedTask;
    }

    private IEnumerable<Measurement<int>> Observe()
    {
        var last = _last;
        if (last is null || _time.GetUtcNow() - last.At > FreshFor)
            return [];
        return last.Checks.Select(c => new Measurement<int>(c.Status, new KeyValuePair<string, object>("check", c.Check)));
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex CheckRule();

    public void Dispose() => _meter.Dispose();

    #endregion
}
