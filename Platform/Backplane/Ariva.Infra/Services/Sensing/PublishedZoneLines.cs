using Ariva.Core.Domain.Enums;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Sensing;

/// <summary>
/// The entry and exit lines of the queue zones in sites' published zone profiles, by site and zone name (ARV-259e4): what
/// devices' line declarations are read against. Two queries: the published profiles the caller selects (within its sites),
/// then their entry and exit lines, of the named zones only when the caller names them, at most <see cref="MaxLines"/>
/// (CWE-400): beyond it the read is cut and <see cref="Truncated"/> says so.
/// </summary>
internal sealed class PublishedZoneLines
{
    /// <summary>The most entry and exit lines one read holds: twenty sites at a profile's limit of 1,000 lines.</summary>
    public const int MaxLines = 20_000;

    private static readonly IReadOnlySet<string> NoLines = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _sites = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Site, string Zone), HashSet<string>> _lines = [];

    private PublishedZoneLines()
    {
    }

    /// <summary>True when the read held more lines than <see cref="MaxLines"/>: the zones it cut are missing.</summary>
    public bool Truncated { get; private set; }

    /// <summary>True when the site has a published zone profile.</summary>
    public bool Published(string site) => site is not null && _sites.Contains(site);

    /// <summary>The entry and exit lines of the site's published queue zone of that name; empty when it holds no such zone.</summary>
    public IReadOnlySet<string> Of(string site, string zone) =>
        site is not null && zone is not null && _lines.TryGetValue((site, zone), out var lines) ? lines : NoLines;

    /// <summary>Every published queue zone with its entry and exit lines, by site and then zone name, in ordinal order.</summary>
    public IEnumerable<(string Site, string Zone, IReadOnlySet<string> Lines)> Zones =>
        _lines.OrderBy(z => z.Key.Site, StringComparer.Ordinal).ThenBy(z => z.Key.Zone, StringComparer.Ordinal)
            .Select(z => (z.Key.Site, z.Key.Zone, (IReadOnlySet<string>)z.Value));

    /// <summary>
    /// Reads the published profiles among <paramref name="profiles"/> and their entry and exit lines, of the queue zones
    /// named in <paramref name="zones"/> when it is given (the zones of the devices shown), at most <paramref name="maxLines"/>.
    /// </summary>
    public static async Task<PublishedZoneLines> LoadAsync(IQueryable<ZoneProfile> profiles, IQueryable<Line> lines, IReadOnlyCollection<string> zones,
        CancellationToken ct, int maxLines = MaxLines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        var found = new PublishedZoneLines();
        var published = await profiles.Where(p => p.Status == ZoneProfileStatus.Published).Select(p => new { p.Id, p.SiteCode }).ToListAsync(ct);
        if (published.Count == 0)
            return found;
        found._sites.UnionWith(published.Select(p => p.SiteCode));
        var ids = published.Select(p => p.Id).ToList();
        var selected = lines.Where(l => ids.Contains(l.Profile.Id) && (l.Role == LineRole.Entry || l.Role == LineRole.Exit) && l.Zone != null);
        if (zones is not null)
            selected = selected.Where(l => zones.Contains(l.Zone.Name));
        var rows = await selected.OrderBy(l => l.Profile.SiteCode).ThenBy(l => l.Zone.Name).ThenBy(l => l.Name)
            .Select(l => new { l.Profile.SiteCode, Zone = l.Zone.Name, l.Name }).Take(maxLines + 1).ToListAsync(ct);
        found.Truncated = rows.Count > maxLines;
        if (found.Truncated)
        {
            // A zone cut part way would read as having fewer lines: leave out the last zone the read reached.
            var last = rows[maxLines - 1];
            rows = [.. rows.Take(maxLines).Where(r => r.SiteCode != last.SiteCode || r.Zone != last.Zone)];
        }

        foreach (var row in rows)
        {
            if (!found._lines.TryGetValue((row.SiteCode, row.Zone), out var zoneLines))
                found._lines[(row.SiteCode, row.Zone)] = zoneLines = new HashSet<string>(StringComparer.Ordinal);
            zoneLines.Add(row.Name);
        }

        return found;
    }
}
