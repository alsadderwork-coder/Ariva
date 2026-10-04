using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Live;

/// <summary>
/// The queue zones a screen may join (ARV-035): those of the site's published zone profile. The names are cached for
/// a minute per site (a zone published or retired is joinable or refused within a minute); a site without a published
/// profile is remembered for 5 seconds only, so invented site codes do not fill the cache.
/// </summary>
public class LiveZoneDirectory(IUnitOfWork unitOfWork, IFusionCache cache)
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan KeepEmpty = TimeSpan.FromSeconds(5);

    public virtual async Task<bool> ContainsAsync(string zoneKey, CancellationToken ct)
    {
        if (!LiveZones.IsZoneKey(zoneKey))
            return false;
        var site = LiveZones.SiteOf(zoneKey);
        var names = await cache.GetOrSetAsync<string[]>(
            $"live:zones:{site}",
            async (context, token) =>
            {
                var published = await unitOfWork.StorageProvider.Query<ZoneProfile>()
                    .Where(p => p.SiteCode == site && p.Status == ZoneProfileStatus.Published)
                    .OrderByDescending(p => p.Version)
                    .Select(p => p.Id)
                    .FirstOrDefaultAsync(token);
                string[] found = published == Guid.Empty
                    ? []
                    : [.. await unitOfWork.StorageProvider.Query<Zone>()
                        .Where(z => z.Profile.Id == published && z.Kind == ZoneKind.Queue)
                        .Select(z => z.Name)
                        .ToListAsync(token)];
                if (found.Length == 0)
                    context.Options.Duration = KeepEmpty;
                return found;
            },
            // Part of the hub's join decision: no answer from an expired copy (ARV-081).
            options => Ariva.Infra.Security.SecurityCacheOptions.Apply(options, Keep),
            token: ct);
        return Array.IndexOf(names, LiveZones.NameOf(zoneKey)) >= 0;
    }
}
