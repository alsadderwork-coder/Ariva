using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>
/// A site's advertising policy as a request (ARV-145): whether the site's displays may show advertising, the queue zones
/// where it stays off regardless, and the reason, which names who authorised the change (the border authority or the
/// airport operator) and their reference. The whole policy is sent each time. <c>ReviewedProfileId</c> is the published zone
/// profile the administrator reviewed the exclusions against (the view's <c>PublishedProfileId</c>): a change that allows
/// advertising is refused (409) when another profile has been published since, so it never confirms geometry nobody saw.
/// </summary>
public sealed record AdvertisingPolicyRequest(
    bool Allowed,
    [MaxLength(20)] IReadOnlyList<string> ExcludedZones,
    [Required, MaxLength(500)] string Reason,
    Guid? ReviewedProfileId = null);

/// <summary>
/// A site's advertising policy as the API shows it. A site that never had one shows advertising off, without a reason.
/// <c>PublishedProfileId</c> and <c>PublishedProfileVersion</c> are the site's published zone profile now (a change names
/// the id it was reviewed against) and <c>PublishedQueueZones</c> its queue zones, read with it, which the screen offers to
/// exclude; <c>ConfirmedProfileVersion</c> the one the policy was last saved against.
/// <c>ProfileChanged</c> is true when the site has published another zone profile since an allowing policy was saved: no
/// display of the site shows advertising until an administrator saves it again (a zone renamed, moved, dropped or added in
/// the new profile never shifts an exclusion on its own). <c>UnpublishedZones</c> are excluded zones that are not queue
/// zones of the published profile. <c>DisplaysFallingBack</c> are the site's displays set to advertising that show
/// instructions because of this policy.
/// </summary>
public sealed record AdvertisingPolicyViewModel(
    string SiteCode,
    bool Allowed,
    IReadOnlyList<string> ExcludedZones,
    string Reason,
    DateTime? ChangedOn,
    IReadOnlyList<string> UnpublishedZones,
    IReadOnlyList<string> DisplaysFallingBack,
    bool ProfileChanged = false,
    Guid? PublishedProfileId = null,
    int? PublishedProfileVersion = null,
    int? ConfirmedProfileVersion = null,
    IReadOnlyList<string> PublishedQueueZones = null);

/// <summary>The answers of the advertising policy service that are not plain validation.</summary>
public static class AdvertisingPolicyErrors
{
    public const string UnknownZones = "Every excluded zone must be a queue zone of the site's published zone profile.";
    public const string Required = "A policy is required: whether advertising is allowed, the excluded zones and the reason.";

    /// <summary>Another zone profile was published since the administrator reviewed the policy (409): review it again.</summary>
    public const string ProfileChangedSinceReview =
        "Another zone profile has been published since you reviewed this policy; check the excluded zones against it and save again.";

    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { ProfileChangedSinceReview };

    /// <summary>The answer of an advertising endpoint for a site whose policy does not allow advertising (403).</summary>
    public const string Off = "Advertising is off for this site.";
}

/// <summary>
/// Advertising policies of the sites (ARV-145), within the caller's sites (another site answers like one that does not
/// exist). Reading needs <c>AdvertisingPolicy.View</c>; changing needs <c>AdvertisingPolicy.Edit</c> (system administrators)
/// and a recent second factor, and is audited with the reason.
/// </summary>
public interface ISvcAdvertisingPolicies : ISvcScoped
{
    Task<Result<AdvertisingPolicyViewModel>> GetAsync(string siteCode, CancellationToken ct = default);
    Task<Result<AdvertisingPolicyViewModel>> ChangeAsync(string siteCode, AdvertisingPolicyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Whether advertising is allowed for the site at all (not for a given zone), for the advertising endpoints: false for a
    /// site without a policy and for one outside the caller's sites.
    /// </summary>
    Task<bool> IsAllowedAsync(string siteCode, CancellationToken ct = default);
}
