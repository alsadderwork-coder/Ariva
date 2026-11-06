using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// Advertising policies of the sites (ARV-145). A site without a policy has advertising off. A change is made in one of the
/// caller's sites (another answers like a site that does not exist, CWE-863), names only queue zones of the site's published
/// profile, and is audited with its reason; the endpoint asks for <c>AdvertisingPolicy.Edit</c> and a recent second factor.
/// A change and a display set to advertising take the same site lock (<see cref="AdvertisingPolicyGuard"/>); the board reads
/// the policy on every refresh and falls back to instructions where it does not allow advertising. ARV-273: a change names the
/// version of the policy it was made from (its last change), checked under that lock, so a change made over another unseen is
/// refused.
/// </summary>
internal sealed class SvcAdvertisingPolicies(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    AdvertisingPolicyGuard guard)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAdvertisingPolicies
{
    private const string Target = "AdvertisingPolicy";

    public async Task<Result<AdvertisingPolicyViewModel>> GetAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingPolicyViewModel>(TopologyErrors.NotFound);
        var policy = await QueryAsNoTracking<AdvertisingPolicy>().Where(p => p.SiteCode == siteCode).FirstOrDefaultAsync(ct);
        return new Result<AdvertisingPolicyViewModel>(await ViewAsync(siteCode, policy, ct));
    }

    public async Task<Result<AdvertisingPolicyViewModel>> ChangeAsync(string siteCode, AdvertisingPolicyRequest request, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingPolicyViewModel>(TopologyErrors.NotFound);
        if (request is null)
            return Result.Error<AdvertisingPolicyViewModel>(AdvertisingPolicyErrors.Required);
        var values = new AdvertisingPolicyValues(request.Allowed, request.ExcludedZones ?? [], request.Reason);
        var problems = values.Problems();
        if (problems.Count > 0)
            return Result.Error<AdvertisingPolicyViewModel>(problems);
        var policy = await guard.LockAsync(siteCode, ct);
        // ARV-273: the policy as stored must be the version the administrator changed; the lock orders two changes, so the
        // second, made from the version the first replaced, is refused rather than overwriting it unseen.
        if (Microseconds(Version(policy)) != Microseconds(request.PolicyChangedOn))
            return Result.Error<AdvertisingPolicyViewModel>(AdvertisingPolicyErrors.ChangedSinceLoaded);
        // The exclusions are judged against the profile published now, which must be the one the administrator reviewed
        // when the change allows advertising (security review of ARV-145, round 3: a profile published meanwhile is never
        // confirmed unseen); a newer one keeps advertising off until the policy is saved again.
        var profile = await guard.PublishedProfileIdAsync(siteCode, ct);
        if (values.Allowed && request.ReviewedProfileId != profile)
            return Result.Error<AdvertisingPolicyViewModel>(AdvertisingPolicyErrors.ProfileChangedSinceReview);
        var zones = values.ExcludedZones.Select(z => z.Trim()).ToList();
        if (zones.Count > 0 && (await guard.QueueZonesOfProfileAsync(profile, zones, ct)).Count != zones.Count)
            return Result.Error<AdvertisingPolicyViewModel>(AdvertisingPolicyErrors.UnknownZones);
        if (policy is null)
        {
            policy = new AdvertisingPolicy(siteCode, values, profile);
            await SaveAsync(policy, ct);
            await audit.RecordAsync($"{Target}.Changed", Target, policy.Id, siteCode, null, policy.AuditSummary(), ct);
        }
        else
        {
            var before = policy.AuditSummary();
            policy.Change(values, profile);
            await UpdateAsync(policy, ct);
            // The session stamps the change's time when it flushes: now, so the answer names the version a later read gives.
            await FlushAsync(ct);
            await audit.RecordAsync($"{Target}.Changed", Target, policy.Id, siteCode, before, policy.AuditSummary(), ct);
        }

        return new Result<AdvertisingPolicyViewModel>(await ViewAsync(siteCode, policy, ct));
    }

    public async Task<bool> IsAllowedAsync(string siteCode, CancellationToken ct = default) =>
        await AccessAsync(siteCode, ct) == AdvertisingAccess.Allowed;

    public async Task<AdvertisingAccess> AccessAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return AdvertisingAccess.NotFound;
        return await QueryAsNoTracking<AdvertisingPolicy>().AnyAsync(p => p.SiteCode == siteCode && p.Allowed, ct)
            ? AdvertisingAccess.Allowed
            : AdvertisingAccess.Off;
    }

    private async Task<bool> VisibleSiteAsync(string siteCode, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(siteCode) && (await siteScope.GetAsync(ct)).Allows(siteCode) &&
        await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == siteCode, ct);

    private async Task<AdvertisingPolicyViewModel> ViewAsync(string siteCode, AdvertisingPolicy policy, CancellationToken ct)
    {
        var excluded = policy?.ExcludedZoneList ?? [];
        // One profile for everything the view says: its version, its queue zones (the ones the screen offers to exclude) and
        // the rule each display is judged by (security review of ARV-145, round 4).
        var (profile, version) = await guard.PublishedProfileAsync(siteCode, ct);
        var published = await guard.AllQueueZonesOfProfileAsync(profile, ct);
        var advertising = await QueryAsNoTracking<Display>()
            .Where(d => d.SiteCode == siteCode && d.DeletedOn == null && d.SecondRegion == DisplaySecondRegion.Advertising)
            .OrderBy(d => d.Code)
            .Take(SvcDisplays.MaxDisplaysPerSite)
            .ToListAsync(ct);
        var fallingBack = advertising.Where(d => policy is null || !policy.AllowsDisplayOf(d.EntryList.Select(e => e.Zone), published, profile))
            .Select(d => d.Code).ToList();
        return new AdvertisingPolicyViewModel(siteCode, policy?.Allowed ?? false, excluded, policy?.Reason, Microseconds(Version(policy)),
            excluded.Where(z => !Enumerable.Contains(published, z, StringComparer.Ordinal)).ToList(), fallingBack,
            policy is { Allowed: true } && policy.ProfileChangedSince(profile), profile, version, await guard.ProfileVersionAsync(policy?.ConfirmedProfileId, ct),
            published);
    }

    /// <summary>ARV-273: the policy's version, its last change (null for a site without one).</summary>
    private static DateTime? Version(AdvertisingPolicy policy) => policy?.ModifiedOn ?? policy?.CreatedOn;

    /// <summary>A time in UTC to the microsecond, as PostgreSQL keeps it, so an answer names the version a later read gives.</summary>
    private static DateTime? Microseconds(DateTime? at)
    {
        if (at is not { } time)
            return null;
        var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }
}
