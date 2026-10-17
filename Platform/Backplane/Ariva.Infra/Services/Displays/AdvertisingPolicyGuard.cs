using Ariva.Core.Domain.Enums;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The advertising rule of a site's displays (ARV-145), in one place for the display service, the board and the policy's
/// own view: a display may show advertising when the site's policy allows it, the site's published zone profile is the one
/// the policy was saved against (any newer profile keeps advertising off on every display of the site until an
/// administrator saves the policy again), every excluded zone is a published queue zone of the site, and none of the
/// display's zones is excluded. <see cref="LockAsync"/> takes the site's lock (a transaction-scoped advisory
/// lock, class 145, the site code's hash, released at the commit) before reading the policy as committed: a change of the
/// policy and a display being set to advertising take it, so neither reads the other half-done (script 0057's trigger
/// checks the same rule in the database).
/// </summary>
internal sealed class AdvertisingPolicyGuard(IUnitOfWork unitOfWork) : SvcDb(unitOfWork)
{
    public async Task<AdvertisingPolicy> LockAsync(string siteCode, CancellationToken ct)
    {
        await FlushAsync(ct);
        await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM (SELECT pg_advisory_xact_lock(145, hashtext(:site))) l""",
            new Dictionary<string, object> { ["site"] = siteCode }, ct);
        return await Query<AdvertisingPolicy>().Where(p => p.SiteCode == siteCode).FirstOrDefaultAsync(ct);
    }

    /// <summary>Whether a display of the site showing these zones may show advertising now, without the lock (the board).</summary>
    public async Task<bool> AllowsAsync(string siteCode, IEnumerable<string> zones, CancellationToken ct) =>
        await AllowsAsync(await QueryAsNoTracking<AdvertisingPolicy>().Where(p => p.SiteCode == siteCode).FirstOrDefaultAsync(ct), zones, ct);

    /// <summary>Whether a display showing these zones may show advertising under this policy (none: off).</summary>
    public async Task<bool> AllowsAsync(AdvertisingPolicy policy, IEnumerable<string> zones, CancellationToken ct)
    {
        if (policy is not { Allowed: true })
            return false;
        // The zones of the profile read here, not of whatever is published by the next statement: the excluded ones and the
        // display's own.
        var profile = await PublishedProfileIdAsync(policy.SiteCode, ct);
        var shown = (zones ?? []).Where(zone => zone is not null).Select(zone => zone.Trim()).ToList();
        return policy.AllowsDisplayOf(shown, await QueueZonesOfProfileAsync(profile, [.. policy.ExcludedZoneList, .. shown], ct), profile);
    }

    /// <summary>The site's published zone profile (one per site, a unique index), or none.</summary>
    public async Task<Guid?> PublishedProfileIdAsync(string siteCode, CancellationToken ct) => (await PublishedProfileAsync(siteCode, ct)).Id;

    /// <summary>The site's published zone profile with its version, or none.</summary>
    public async Task<(Guid? Id, int? Version)> PublishedProfileAsync(string siteCode, CancellationToken ct)
    {
        var found = await QueryAsNoTracking<ZoneProfile>()
            .Where(p => p.SiteCode == siteCode && p.Status == ZoneProfileStatus.Published)
            .Select(p => new { p.Id, p.Version })
            .ToListAsync(ct);
        return found.Count == 0 ? (null, null) : (found[0].Id, found[0].Version);
    }

    /// <summary>A zone profile's version (any status), or none.</summary>
    public async Task<int?> ProfileVersionAsync(Guid? profileId, CancellationToken ct) =>
        profileId is null
            ? null
            : (await QueryAsNoTracking<ZoneProfile>().Where(p => p.Id == profileId).Select(p => p.Version).ToListAsync(ct)).FirstOrDefault();

    /// <summary>Every queue zone of that zone profile by name, in order (none without a profile).</summary>
    public async Task<IReadOnlyList<string>> AllQueueZonesOfProfileAsync(Guid? profileId, CancellationToken ct) =>
        profileId is null
            ? []
            : (await QueryAsNoTracking<Zone>()
                .Where(z => z.Profile.Id == profileId && z.Kind == ZoneKind.Queue)
                .Select(z => z.Name)
                .ToListAsync(ct)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>Which of these names are queue zones of that zone profile, each once (none without a profile).</summary>
    public async Task<IReadOnlyList<string>> QueueZonesOfProfileAsync(Guid? profileId, IReadOnlyCollection<string> names, CancellationToken ct) =>
        profileId is null || names is null || names.Count == 0
            ? []
            : (await QueryAsNoTracking<Zone>()
                .Where(z => z.Profile.Id == profileId && names.Contains(z.Name) && z.Kind == ZoneKind.Queue)
                .Select(z => z.Name)
                .ToListAsync(ct)).Distinct(StringComparer.Ordinal).ToList();

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
