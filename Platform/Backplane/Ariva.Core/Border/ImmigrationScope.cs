namespace Ariva.Core.Border;

/// <summary>
/// Which sites an immigration call may write (ARV-048): one site (<see cref="ForSite"/>, the REST path's site; a record
/// naming another site is refused), or any site Ariva knows (<see cref="RecordSite"/>, AMAN's Kafka feed, where each
/// record names its site; an unknown site is refused). The default value is neither, and the intake refuses it, so a
/// caller that forgets to choose fails closed.
/// </summary>
public readonly record struct ImmigrationScope
{
    private ImmigrationScope(string siteCode, bool anyKnownSite)
    {
        SiteCode = siteCode;
        AnyKnownSite = anyKnownSite;
    }

    /// <summary>The one site the call may write, or null for <see cref="RecordSite"/>.</summary>
    public string SiteCode { get; }

    /// <summary>True for <see cref="RecordSite"/>.</summary>
    public bool AnyKnownSite { get; }

    /// <summary>True when the scope was chosen with <see cref="ForSite"/> or <see cref="RecordSite"/>.</summary>
    public bool IsChosen => SiteCode is not null || AnyKnownSite;

    /// <summary>The transport, for metrics: <c>rest</c> for one site, <c>kafka</c> for the record's site.</summary>
    public string Transport => AnyKnownSite ? "kafka" : "rest";

    public static ImmigrationScope ForSite(string siteCode) => new(siteCode ?? throw new ArgumentNullException(nameof(siteCode)), false);

    /// <summary>Each record's own site, which must be a site Ariva knows: AMAN's Kafka feed only.</summary>
    public static ImmigrationScope RecordSite { get; } = new(null, true);
}
