namespace Ariva.Core.Services.Displays;

/// <summary>
/// The display stale sweep (ARV-259d, wiki 10 section 3's "display stale"): every sweep judges each enabled display as its
/// board would be judged now, and counts per site those whose board is stale (a queue zone's live snapshot missing or older
/// than the display's stale threshold). One replica of Ariva.Api.Main a round; nothing is written. System calls (no user).
/// </summary>
public interface ISvcDisplayStaleness : ISvcScoped
{
    Task<DisplayStaleSweep> SweepAsync(CancellationToken ct = default);
}

/// <summary>
/// What a sweep found: whether it ran (one replica a round), per site the displays judged and those stale, whether more
/// enabled displays exist than one sweep judges, and whether the live snapshots could not be read (every display not judged
/// by then counted as stale).
/// </summary>
public sealed record DisplayStaleSweep(bool Ran, IReadOnlyList<DisplayStaleSite> Sites, bool Truncated, bool SnapshotsUnreadable)
{
    /// <summary>Another replica swept this round.</summary>
    public static DisplayStaleSweep Skipped { get; } = new(false, [], false, false);
}

/// <summary>A site's enabled displays judged by a sweep and those whose board is stale.</summary>
public sealed record DisplayStaleSite(string SiteCode, int Displays, int Stale);
