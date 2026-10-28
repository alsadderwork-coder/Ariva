using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Resilience;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ariva.Infra.Sensing;

/// <summary>The device health settings (<c>Devices:Health</c>, ARV-025).</summary>
public sealed class DeviceHealthSettings
{
    public const string SectionName = "Devices:Health";

    /// <summary>
    /// A commissioned device not heard from for this long goes Offline. Ingest reports every status a device sends and,
    /// while it sends data, its health every 10 seconds, so a device must push or report at least every half of this.
    /// </summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 180;

    /// <summary>How often the heartbeat monitor sweeps.</summary>
    public int SweepSeconds { get; set; } = 15;

    public bool IsValid => HeartbeatTimeoutSeconds is >= 30 and <= 3_600 && SweepSeconds is >= 5 and <= 300 && SweepSeconds < HeartbeatTimeoutSeconds;
}

/// <summary>
/// Device health metrics (meter <c>Ariva.Devices</c>): heartbeats lost, recoveries and zones that became degraded, per
/// site; and, from the last sweep, devices monitored (ARV-125a: commissioned devices whose health is watched, Online,
/// Degraded or Offline), offline and degraded, zones degraded, and devices whose clock is unreliable (ARV-259a). Only the replica of Ariva.Api.Main that wins a sweep's
/// lock computes the totals, so they are reported only while that sweep is fresh (<see cref="Snapshot"/>'s window): the
/// other hosts, which register this class without sweeping, and a replica that has not won a sweep lately report nothing,
/// and a dashboard takes the largest of the copies, never their sum.
/// </summary>
public sealed class DeviceHealthMetrics : IDisposable
{
    public const string MeterName = "Ariva.Devices";

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private readonly Counter<long> _lost;
    private readonly Counter<long> _recovered;
    private readonly Counter<long> _zonesDegraded;
    private readonly Counter<long> _zonesNotKeyable;
    private readonly ConcurrentDictionary<string, byte> _notKeyable = new(StringComparer.Ordinal);
    private volatile Totals _totals;

    /// <summary>Zones remembered as warned about; beyond this many (never in practice) each further one warns every time.</summary>
    internal const int MaxRememberedZones = 1_000;

    private sealed record Totals(int Monitored, int Offline, int Degraded, int Zones, int ClockUnreliable, DateTimeOffset At, TimeSpan FreshFor);

    public DeviceHealthMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _lost = _meter.CreateCounter<long>("ariva.devices.heartbeat_lost", description: "Commissioned devices marked Offline after missing their heartbeat");
        _recovered = _meter.CreateCounter<long>("ariva.devices.recovered", description: "Offline devices heard from again");
        _zonesDegraded = _meter.CreateCounter<long>("ariva.zones.degraded", description: "Queue zones that became Degraded");
        _zonesNotKeyable = _meter.CreateCounter<long>("ariva.zones.not_keyable",
            description: "Queue zone assessments skipped because the zone key is longer than a message key may be (ARV-114c)");
        _meter.CreateObservableGauge("ariva.devices.monitored", () => Fresh(t => t.Monitored),
            description: "Commissioned devices whose health is watched (Online, Degraded or Offline) at the last sweep");
        _meter.CreateObservableGauge("ariva.devices.offline", () => Fresh(t => t.Offline), description: "Commissioned devices Offline at the last sweep");
        _meter.CreateObservableGauge("ariva.devices.degraded", () => Fresh(t => t.Degraded), description: "Commissioned devices Degraded at the last sweep");
        _meter.CreateObservableGauge("ariva.zones.degraded_now", () => Fresh(t => t.Zones), description: "Queue zones Degraded at the last sweep");
        _meter.CreateObservableGauge("ariva.devices.clock_unreliable", () => Fresh(t => t.ClockUnreliable),
            description: "Commissioned devices heard within the heartbeat timeout whose clock is unreliable (off by more than 500 ms and unstable, F19) at the last sweep");
    }

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    public void HeartbeatLost(string site) => _lost.Add(1, new KeyValuePair<string, object>("site", site));

    public void Recovered(string site) => _recovered.Add(1, new KeyValuePair<string, object>("site", site));

    public void ZoneDegraded(string site) => _zonesDegraded.Add(1, new KeyValuePair<string, object>("site", site));

    /// <summary>
    /// Counts a zone whose health is not assessed because its key does not fit a message key (ARV-114c); true the first
    /// time this process sees the zone, so the caller warns once per zone instead of on every sweep.
    /// </summary>
    public bool ZoneNotKeyable(string site, string zoneKey)
    {
        _zonesNotKeyable.Add(1, new KeyValuePair<string, object>("site", site));
        if (_notKeyable.ContainsKey(zoneKey))
            return false;
        return _notKeyable.Count >= MaxRememberedZones || _notKeyable.TryAdd(zoneKey, 0);
    }

    /// <summary>The totals of a sweep that just ran, reported for <paramref name="freshFor"/> (two sweep intervals).</summary>
    public void Snapshot(int monitored, int offline, int degraded, int zones, int clockUnreliable, TimeSpan freshFor) =>
        _totals = new Totals(monitored, offline, degraded, zones, clockUnreliable, _time.GetUtcNow(), freshFor);

    /// <summary>The last sweep's totals (zeros before the first), fresh or not.</summary>
    public (int Monitored, int Offline, int Degraded, int Zones, int ClockUnreliable) Current =>
        _totals is { } t ? (t.Monitored, t.Offline, t.Degraded, t.Zones, t.ClockUnreliable) : (0, 0, 0, 0, 0);

    private IEnumerable<Measurement<int>> Fresh(Func<Totals, int> value)
    {
        var totals = _totals;
        return totals is not null && _time.GetUtcNow() - totals.At <= totals.FreshFor ? [new Measurement<int>(value(totals))] : [];
    }

    public void Dispose() => _meter.Dispose();
}

/// <summary>Records every device health report from <c>ariva.device.health.v1</c> (ARV-025), through the Ariva consume pipe.</summary>
public sealed class DeviceHealthConsumer(ISvcDeviceHealth health) : IConsumer<DeviceHealthReported>
{
    public Task Consume(ConsumeContext<DeviceHealthReported> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return health.RecordAsync(context.Message, context.CancellationToken);
    }
}

/// <summary>
/// Sweeps for devices that lost their heartbeat every <see cref="DeviceHealthSettings.SweepSeconds"/> (ARV-025); a sweep ends at
/// <see cref="RunTimeout"/> (ARV-096e) and is rolled back, and the next one sweeps again.
/// </summary>
internal sealed class DeviceHeartbeatMonitor(IServiceScopeFactory scopes, IOptions<DeviceHealthSettings> settings, TimeProvider timeProvider,
    ILogger<DeviceHeartbeatMonitor> logger) : BackgroundService, IBoundedRuns
{
    /// <summary>The last sweep's outcome (tests and diagnostics).</summary>
    internal DeviceHealthSweep Last { get; private set; }

    /// <summary>One minute: a sweep is a few statements over the commissioned devices.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>One sweep under the run timeout (tests).</summary>
    internal Task RunOnceAsync(CancellationToken stoppingToken) => RunBound.RunAsync("Device heartbeat sweep", RunTimeout, timeProvider, async ct =>
    {
        await using var scope = scopes.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            Last = await scope.ServiceProvider.GetRequiredService<ISvcDeviceHealth>().SweepAsync(ct);
            unitOfWork.PromiseToCommit();
            await unitOfWork.EndAsync(ct);
        }
        catch
        {
            // A sweep that failed or was cut part way is rolled back (a promised unit would otherwise commit as the scope ends).
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }, stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.Value.SweepSeconds), timeProvider);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one failed sweep (the database restarting) must not stop the monitor; the next one retries
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Device heartbeat sweep failed; retrying at the next interval");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
