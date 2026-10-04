using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Services.Sensing;

/// <summary>
/// The registry as devices see it (ARV-022), for Ariva.Api.Ingest. The credential lookup is cached in memory for at
/// most a minute under the "devices" tag, which every registry change evicts after commit on every host through the
/// backplane, so a rotated or revoked credential stops working at once where the backplane runs and within a minute
/// otherwise; never longer, because fail-safe is off for it. A lookup that finds nothing is cached too (in memory only),
/// so a flood of made-up prefixes costs one query each per minute. A lookup the database does not answer within
/// <see cref="LookupTimeout"/> fails with a timeout (503 to the device, ARV-072).
/// </summary>
internal sealed class SvcDeviceGateway(IUnitOfWork unitOfWork, IFusionCache cache) : SvcDb(unitOfWork), ISvcDeviceGateway
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);

    /// <summary>
    /// ARV-072: how long a push waits for the database to look a credential up. A stalled database answers the device
    /// 503 with Retry-After after this (DependencyOutage) instead of holding the request for Npgsql's 30 second command
    /// timeout, so sensors back off and push again rather than piling up connections.
    /// </summary>
    internal static readonly TimeSpan LookupTimeout = Ariva.Infra.Security.SecurityCacheOptions.LookupTimeout;

    public async Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(prefix))
            return null;
        return await cache.GetOrSetAsync<DeviceCredentialRecord>($"devices:prefix:{prefix}", async (_, token) =>
        {
            var device = await QueryAsNoTracking<Device>()
                .Where(d => d.CredentialPrefix == prefix && d.CredentialHash != null && d.State != DeviceState.Retired)
                .FirstOrDefaultAsync(token);
            return device is null
                ? null
                : new DeviceCredentialRecord(device.Id.GetValueOrDefault(), device.Code, device.SiteCode, device.QueueZoneName, device.State.ToString(),
                    device.CredentialHash, string.IsNullOrEmpty(device.AllowedSources) ? [] : device.AllowedSources.Split(','), device.ClientCertificateSha256,
                    device.Dialect.ToString(), device.X, device.Y, device.OrientationDegrees, device.MappingName,
                    device.Transport.ToString());
        }, options =>
        {
            // A credential decision must never come from a stale copy: no fail-safe (which would serve an expired entry
            // for up to an hour when the database is slow or down), no soft timeout, and memory only (no copy of the
            // hash, and no misses, in Redis). Eviction still reaches every host through the backplane's tag removal.
            Ariva.Infra.Security.SecurityCacheOptions.Apply(options, Duration);
            options.SkipDistributedCacheRead = true;
            options.SkipDistributedCacheWrite = true;
        }, tags: [SvcDevices.CacheTag], token: ct);
    }

    public async Task<Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default)
    {
        var profile = await QueryAsNoTracking<ZoneProfile>()
            .Where(p => p.SiteCode == siteCode && p.Status == ZoneProfileStatus.Published)
            .FirstOrDefaultAsync(ct);
        var queue = profile?.Zones.FirstOrDefault(z => z.Kind == ZoneKind.Queue && string.Equals(z.Name, queueZoneName, StringComparison.Ordinal));
        if (queue is null)
            return Result.Error<DeviceZoneViewModel>(TopologyErrors.NotFound);

        var zones = profile.Zones.Where(z => z == queue || z.QueueZone == queue).OrderBy(z => z.Name, StringComparer.Ordinal).ToList();
        var lines = profile.Lines.Where(l => l.Zone is not null && zones.Contains(l.Zone)).OrderBy(l => l.Name, StringComparer.Ordinal).ToList();
        return new Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queue.Name, profile.Version.GetValueOrDefault(), profile.GeometryHash,
            [.. zones.Select(z => new ZoneViewModel(z.Id.GetValueOrDefault(), z.Name, z.Kind.ToString(), z.LevelId, z.QueueZone?.Id, z.DeskId, z.Polygon, Math.Round(z.AreaSquareMetres, 3)))],
            [.. lines.Select(l => new LineViewModel(l.Id.GetValueOrDefault(), l.Name, l.Role.ToString(), l.Zone?.Id, l.LevelId, l.StartX, l.StartY, l.EndX, l.EndY, Math.Round(l.LengthMetres, 3)))]));
    }
}
