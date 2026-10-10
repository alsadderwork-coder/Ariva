using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
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
/// site; and, from the last sweep, devices offline and degraded and zones degraded.
/// </summary>
public sealed class DeviceHealthMetrics : IDisposable
{
    public const string MeterName = "Ariva.Devices";

    private readonly Meter _meter;
    private readonly Counter<long> _lost;
    private readonly Counter<long> _recovered;
    private readonly Counter<long> _zonesDegraded;
    private readonly Counter<long> _zonesNotKeyable;
    private readonly ConcurrentDictionary<string, byte> _notKeyable = new(StringComparer.Ordinal);
    private volatile Totals _totals = new(0, 0, 0);

    /// <summary>Zones remembered as warned about; beyond this many (never in practice) each further one warns every time.</summary>
    internal const int MaxRememberedZones = 1_000;

    private sealed record Totals(int Offline, int Degraded, int Zones);

    public DeviceHealthMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _lost = _meter.CreateCounter<long>("ariva.devices.heartbeat_lost", description: "Commissioned devices marked Offline after missing their heartbeat");
        _recovered = _meter.CreateCounter<long>("ariva.devices.recovered", description: "Offline devices heard from again");
        _zonesDegraded = _meter.CreateCounter<long>("ariva.zones.degraded", description: "Queue zones that became Degraded");
        _zonesNotKeyable = _meter.CreateCounter<long>("ariva.zones.not_keyable",
            description: "Queue zone assessments skipped because the zone key is longer than a message key may be (ARV-114c)");
        _meter.CreateObservableGauge("ariva.devices.offline", () => _totals.Offline, description: "Commissioned devices Offline at the last sweep");
        _meter.CreateObservableGauge("ariva.devices.degraded", () => _totals.Degraded, description: "Commissioned devices Degraded at the last sweep");
        _meter.CreateObservableGauge("ariva.zones.degraded_now", () => _totals.Zones, description: "Queue zones Degraded at the last sweep");
    }

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

    public void Snapshot(int offline, int degraded, int zones) => _totals = new Totals(offline, degraded, zones);

    public (int Offline, int Degraded, int Zones) Current => (_totals.Offline, _totals.Degraded, _totals.Zones);

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

/// <summary>Sweeps for devices that lost their heartbeat every <see cref="DeviceHealthSettings.SweepSeconds"/> (ARV-025).</summary>
internal sealed class DeviceHeartbeatMonitor(IServiceScopeFactory scopes, IOptions<DeviceHealthSettings> settings, TimeProvider timeProvider,
    ILogger<DeviceHeartbeatMonitor> logger) : BackgroundService
{
    /// <summary>The last sweep's outcome (tests and diagnostics).</summary>
    internal DeviceHealthSweep Last { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.Value.SweepSeconds), timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                Last = await scope.ServiceProvider.GetRequiredService<ISvcDeviceHealth>().SweepAsync(stoppingToken);
                unitOfWork.PromiseToCommit();
                await unitOfWork.EndAsync(stoppingToken);
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
