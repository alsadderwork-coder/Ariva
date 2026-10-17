using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ariva.Core.Domain.Components;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// Whether a site's passenger displays may show advertising in their second region (ARV-145, wiki 11 section 9). Many
/// border authorities do not allow commercial advertising in arrivals and immigration halls, so a site without a policy has
/// advertising off. Only a system administrator with a second factor turns it on (owner decision 2026-10-16), giving the
/// reason, which names who authorised it (the border authority or the airport operator); the audit trail keeps every change
/// with who made it and when. Queue zones can be excluded even where the site allows it: a display showing any of them
/// shows instructions instead. Exclusions name zones, and the roles that manage zone profiles can rename, move, drop or add
/// zones in a new profile (a rename can even hand the excluded name to a decoy zone). So a change of the policy confirms the
/// site's published zone profile of that moment, and once another profile is published no display of the site shows
/// advertising until an administrator saves the policy again (security review of ARV-145): only an administrator decides
/// where advertising runs, whatever the geometry becomes.
/// </summary>
public class AdvertisingPolicy : BaseAuditableEntity<AdvertisingPolicy>, ISiteBound
{
    public const int MaxExcludedZones = 20;
    public const int MaxZoneLength = 200;
    public const int MinReasonLength = 10;
    public const int MaxReasonLength = 500;

    /// <summary>The stored JSON's room (script 0057): characters outside the basic plane are stored escaped, 12 for 2.</summary>
    public const int MaxExcludedZonesJsonLength = 8000;

    protected AdvertisingPolicy()
    {
    }

    public AdvertisingPolicy(string siteCode, AdvertisingPolicyValues values, Guid? confirmedProfileId)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        SiteCode = siteCode;
        Change(values, confirmedProfileId);
    }

    public virtual string SiteCode { get; protected set; }

    /// <summary>Whether the site's displays may show advertising at all.</summary>
    public virtual bool Allowed { get; protected set; }

    /// <summary>The queue zone names where advertising stays off even when the site allows it, as a JSON array.</summary>
    [MaxLength(MaxExcludedZonesJsonLength)]
    public virtual string ExcludedZones { get; protected set; }

    /// <summary>Why the last change was made: who authorised it (the border authority or the airport operator) and their reference.</summary>
    [MaxLength(MaxReasonLength)]
    public virtual string Reason { get; protected set; }

    /// <summary>
    /// The site's published zone profile when the policy was last saved, whose zones the exclusions were judged against;
    /// none when the site had no published profile.
    /// </summary>
    public virtual Guid? ConfirmedProfileId { get; protected set; }

    public virtual IReadOnlyList<string> ExcludedZoneList =>
        ExcludedZones is null ? [] : JsonSerializer.Deserialize<List<string>>(ExcludedZones, Display.Json) ?? [];

    /// <summary>Saves the policy against the site's published zone profile of this moment (<paramref name="confirmedProfileId"/>).</summary>
    public virtual void Change(AdvertisingPolicyValues values, Guid? confirmedProfileId)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = values.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(values));
        Allowed = values.Allowed;
        ExcludedZones = ZonesJson(values.ExcludedZones);
        Reason = values.Reason.Trim();
        ConfirmedProfileId = confirmedProfileId;
    }

    /// <summary>Whether the site has published another zone profile since the policy was saved (or has none any more).</summary>
    public virtual bool ProfileChangedSince(Guid? publishedProfileId) => ConfirmedProfileId is null || ConfirmedProfileId != publishedProfileId;

    /// <summary>
    /// Whether a display showing these queue zones may show advertising: the site allows it, the site's published zone
    /// profile (<paramref name="publishedProfileId"/>) is the one the policy was saved against, every excluded zone and every
    /// zone the display shows is a queue zone of that profile (<paramref name="publishedQueueZones"/>, its queue zone names;
    /// others may be left out), and none of the display's zones is excluded. Fails closed: after any new profile, advertising
    /// stays off on every display of the site until an administrator saves the policy again; a display still showing a
    /// zone the profile dropped shows instructions. The same rule as script 0057's trigger.
    /// </summary>
    public virtual bool AllowsDisplayOf(IEnumerable<string> zones, IReadOnlyCollection<string> publishedQueueZones, Guid? publishedProfileId)
    {
        if (!Allowed || ProfileChangedSince(publishedProfileId))
            return false;
        var excluded = ExcludedZoneList;
        var shown = (zones ?? []).Select(zone => zone?.Trim()).ToList();
        if (excluded.Concat(shown).Any(zone => publishedQueueZones is null || !Enumerable.Contains(publishedQueueZones, zone, StringComparer.Ordinal)))
            return false;
        return !shown.Any(zone => Enumerable.Contains(excluded, zone, StringComparer.Ordinal));
    }

    public virtual AdvertisingPolicyValues Values() => new(Allowed, ExcludedZoneList, Reason);

    /// <summary>
    /// What the audit trail keeps of a change: the site, the setting, every excluded zone and the reason. The values are
    /// refused when this would not fit an audit entry whole (<see cref="AdvertisingPolicyValues.Problems"/>), so the reason
    /// a change was authorised is never cut from it.
    /// </summary>
    public virtual string AuditSummary() => SummaryOf(SiteCode, Allowed, ExcludedZoneList, Reason, ConfirmedProfileId);

    internal static string SummaryOf(string siteCode, bool allowed, IEnumerable<string> excludedZones, string reason, Guid? confirmedProfileId) =>
        JsonSerializer.Serialize(new
        {
            SiteCode = siteCode, Allowed = allowed, ExcludedZones = (excludedZones ?? []).Select(z => z?.Trim()), Reason = reason?.Trim(),
            ConfirmedProfileId = confirmedProfileId
        }, Display.Json);

    internal static string ZonesJson(IEnumerable<string> zones) => JsonSerializer.Serialize((zones ?? []).Select(z => z.Trim()).ToList(), Display.Json);
}

/// <summary>A site's advertising policy as checked values (ARV-145).</summary>
public sealed record AdvertisingPolicyValues(bool Allowed, IReadOnlyList<string> ExcludedZones, string Reason)
{
    public const string TooLongToAudit = "The excluded zones and the reason are too long to keep whole in the audit trail; use fewer symbols or a shorter reason.";

    /// <summary>The longest site code (<see cref="Site"/>), so the audit check holds for every site.</summary>
    private const string LongestSiteCode = "XXXXXXXXXXXXXXXXX";

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Trim().Length is < AdvertisingPolicy.MinReasonLength or > AdvertisingPolicy.MaxReasonLength ||
            !DisplayText.IsClean(Reason.Trim()))
            problems.Add($"A reason of {AdvertisingPolicy.MinReasonLength} to {AdvertisingPolicy.MaxReasonLength} characters, on one line, names who authorised the change and their reference.");
        var zones = ExcludedZones ?? [];
        if (zones.Count > AdvertisingPolicy.MaxExcludedZones)
            problems.Add($"At most {AdvertisingPolicy.MaxExcludedZones} queue zones are excluded.");
        if (zones.Any(z => string.IsNullOrWhiteSpace(z) || z.Trim().Length > AdvertisingPolicy.MaxZoneLength || !DisplayText.IsClean(z.Trim())))
            problems.Add("Every excluded zone names a queue zone.");
        else if (zones.Select(z => z.Trim()).Distinct(StringComparer.Ordinal).Count() != zones.Count)
            problems.Add("A queue zone is excluded once.");
        if (problems.Count == 0 && (AdvertisingPolicy.ZonesJson(zones).Length > AdvertisingPolicy.MaxExcludedZonesJsonLength ||
                                    AdvertisingPolicy.SummaryOf(LongestSiteCode, Allowed, zones, Reason, Guid.Empty).Length > AuditEntry.SummaryLength))
            problems.Add(TooLongToAudit);
        return problems;
    }
}
