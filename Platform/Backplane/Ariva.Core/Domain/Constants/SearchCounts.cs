namespace Ariva.Core.Domain.Constants;

/// <summary>
/// ARV-096d (CWE-400, ASVS 5.0 V15.2.2): the searches whose filters need no date range (the audit trail and the alerts) count at
/// most <see cref="Cap"/> matches and say so (<c>PageViewModel.TotalCountCapped</c>) rather than count a whole table that grows
/// with time: they read the one row past the cap (OFFSET <see cref="Cap"/> LIMIT 1, at most <see cref="Cap"/> plus one rows) and count
/// exactly only when there is none. They page through those first matches only: a page that starts past them is refused with <see cref="BeyondCap"/>,
/// since its offset would read as far as a whole count does. A date range, an action, a site or a zone narrows a search below it.
/// </summary>
public static class SearchCounts
{
    /// <summary>The most matches a search counts, and the furthest it pages.</summary>
    public const int Cap = 10_000;

    /// <summary>The answer to a page that starts past <see cref="Cap"/> matches (a fixed text).</summary>
    public const string BeyondCap = "A search pages through its first 10,000 matches only: narrow it, for example with dates.";

    /// <summary>True when a page of this size and index starts past the matches a search counts.</summary>
    public static bool StartsBeyondCap(int pageIndex, int pageSize) => (long)(pageIndex - 1) * pageSize >= Cap;
}
