namespace Ariva.Core.Security;

/// <summary>
/// The sites a caller may access (ARV-012): every site, or a fixed set of codes. Nothing for anonymous, pending and
/// disabled callers.
/// </summary>
public sealed record SiteAccess(bool AllSites, IReadOnlySet<string> SiteCodes)
{
    public static SiteAccess None { get; } = new(false, new HashSet<string>(StringComparer.Ordinal));

    public static SiteAccess Everything { get; } = new(true, new HashSet<string>(StringComparer.Ordinal));

    public bool Allows(string siteCode) => siteCode is not null && (AllSites || SiteCodes.Contains(siteCode));

    /// <summary>True when every site of <paramref name="other"/> is also allowed here (granting needs this, CWE-269).</summary>
    public bool Covers(SiteAccess other) =>
        other is not null && (AllSites || (!other.AllSites && other.SiteCodes.All(SiteCodes.Contains)));
}

/// <summary>
/// The caller's site access (ARV-012). Services filter every site-bound query with
/// <see cref="SiteScopeExtensions.WithinSites{T}"/> and check every site-bound id, answering 404 for a record outside
/// the caller's sites; controllers whose actions take a site, airport or terminal reference carry [SiteScoped].
/// </summary>
public interface ISiteScope : Services.ISvcScoped
{
    Task<SiteAccess> GetAsync(CancellationToken ct = default);
}

public static class SiteScopeExtensions
{
    /// <summary>Only the records of the allowed sites.</summary>
    public static IQueryable<T> WithinSites<T>(this IQueryable<T> query, SiteAccess access) where T : ISiteBound
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.AllSites)
            return query;
        var codes = access.SiteCodes.ToList();
        return query.Where(x => codes.Contains(x.SiteCode));
    }
}
