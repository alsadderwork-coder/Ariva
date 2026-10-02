using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;

namespace Ariva.Core.Services.Sensing;

/// <summary>
/// Device heartbeats and zone degradation (ARV-025). Ariva.Api.Ingest publishes a health report for every status a
/// device sends, or at most every 10 seconds while it sends data; Main records each one as the device's heartbeat. A
/// commissioned device that is not heard from for the heartbeat timeout goes Offline, one that reports itself unwell
/// goes Degraded, and either recovers to Online when it reports again. A queue zone is Degraded while any of its
/// commissioned devices is not online. Every change is published (DeviceRegistryChanged, ZoneHealthChanged).
/// </summary>
public interface ISvcDeviceHealth : ISvcScoped
{
    /// <summary>The health of the devices and zones of the caller's sites (or of one site).</summary>
    Task<Result<DeviceHealthOverviewViewModel>> OverviewAsync(string siteCode, CancellationToken ct = default);

    /// <summary>One device's health; 404 outside the caller's sites.</summary>
    Task<Result<DeviceHealthViewModel>> GetAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>Records a health report from the ingest (system, no user): the heartbeat, a recovery or a degradation.</summary>
    Task RecordAsync(DeviceHealthReported report, CancellationToken ct = default);

    /// <summary>Marks the devices whose heartbeat is older than the timeout Offline and re-assesses every zone (system, no user).</summary>
    Task<DeviceHealthSweep> SweepAsync(CancellationToken ct = default);
}

/// <summary>What a sweep did: devices it marked offline, zones whose health changed, and the totals after it.</summary>
public sealed record DeviceHealthSweep(bool Ran, int MarkedOffline, int ZonesChanged, int DevicesOffline, int DevicesDegraded, int ZonesDegraded);
