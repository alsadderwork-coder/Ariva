namespace Ariva.Core.Services.Live;

public static class OverflowBandErrors
{
    /// <summary>
    /// The published profile has more overflow bands than one answer holds (<see cref="ISvcOverflowBands.MaxBands"/>): the read
    /// is refused, never cut short (CWE-120). A fixed text that echoes nothing of the request.
    /// </summary>
    public const string TooMany = "The site's published zone profile has more overflow bands than one answer holds (500).";
}

/// <summary>
/// One overflow band of a queue zone as the live screens show it (ARV-120): its latest minute with a reading within the
/// last <see cref="ISvcOverflowBands.FreshMinutes"/> minutes (from <c>overflow_minute</c>, written by Ariva.Api.Stream,
/// ARV-115) and the most people its sensors reported in that minute, and the state that follows
/// (<see cref="OverflowBandStates"/>). Without a reading in the window the minute and the occupancy are null.
/// </summary>
public sealed record OverflowBandViewModel(string Band, string State, DateTime? MinuteUtc, int? MaxOccupancy);

/// <summary>A queue zone of the site's published profile that has overflow bands, with its bands and the zone's state.</summary>
public sealed record OverflowZoneViewModel(string Zone, string State, IReadOnlyList<OverflowBandViewModel> Bands);

/// <summary>
/// The overflow bands of a site at <see cref="AsOfUtc"/> (ARV-120): every queue zone of the published profile with at least
/// one overflow band, in name order, each with all its bands. A queue zone without a band is not listed. A profile with more
/// bands than <see cref="ISvcOverflowBands.MaxBands"/> is refused (<see cref="OverflowBandErrors.TooMany"/>), never cut
/// short (a profile holds at most 500 zones, so it never should). Zone-level occupancy only: no desk, no track, no person.
/// </summary>
public sealed record OverflowBandsViewModel(string SiteCode, DateTime AsOfUtc, int FreshMinutes, IReadOnlyList<OverflowZoneViewModel> Zones);

/// <summary>A band of the published profile and its latest reading in the window, as the reader found it (no reading: nulls).</summary>
public sealed record OverflowBandReading(string Zone, string Band, DateTime? MinuteUtc, int? MaxOccupancy);

/// <summary>
/// The states of an overflow band and of its queue zone (ARV-120, Proposed): a band is <see cref="Occupied"/> when its latest
/// minute with a reading in the window held anyone (TC-19: any band's highest reading above zero), <see cref="Empty"/> when
/// that minute's highest reading was 0, and <see cref="NotHeard"/> without a reading in the window (as the stream, a minute
/// without a reading is never read as empty). A queue zone is Occupied when any of its bands is, Empty when every band
/// reported empty, and NotHeard otherwise (a band not heard may hold people).
/// </summary>
public static class OverflowBandStates
{
    public const string Occupied = "Occupied";
    public const string Empty = "Empty";
    public const string NotHeard = "NotHeard";

    /// <summary>The highest occupancy a band minute can hold (the table's check, script 0039).</summary>
    public const int MaxOccupancy = 10_000;

    /// <summary>A band's state from its latest reading; a reading outside 0 to 10,000 is not believed (NotHeard).</summary>
    public static string Of(int? maxOccupancy) => maxOccupancy switch
    {
        null => NotHeard,
        < 0 or > MaxOccupancy => NotHeard,
        > 0 => Occupied,
        _ => Empty
    };

    /// <summary>
    /// The bands the reader returned, whole, or null when they are more than <see cref="ISvcOverflowBands.MaxBands"/>: the read
    /// is refused beyond its bound, never cut short (CWE-120), so no queue zone is ever built from part of its bands. The reader
    /// asks for one row more than the bound to tell the two apart.
    /// </summary>
    public static IReadOnlyList<OverflowBandReading> Within(IReadOnlyList<OverflowBandReading> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count > ISvcOverflowBands.MaxBands ? null : rows;
    }

    /// <summary>A queue zone's state from its bands' states.</summary>
    public static string OfZone(IReadOnlyCollection<string> bandStates)
    {
        ArgumentNullException.ThrowIfNull(bandStates);
        if (bandStates.Any(s => string.Equals(s, Occupied, StringComparison.Ordinal)))
            return Occupied;
        return bandStates.Count > 0 && bandStates.All(s => string.Equals(s, Empty, StringComparison.Ordinal)) ? Empty : NotHeard;
    }

    /// <summary>
    /// The zones of the answer: each band of <paramref name="bands"/> once (the first reading of a zone and band decides),
    /// grouped by queue zone in ordinal name order, bands in ordinal name order. A reading whose minute lies outside
    /// [<paramref name="asOfUtc"/> - <paramref name="freshFor"/>, <paramref name="asOfUtc"/>] counts as no reading, so the
    /// window holds whatever the reader returned.
    /// </summary>
    public static IReadOnlyList<OverflowZoneViewModel> Build(IEnumerable<OverflowBandReading> bands, DateTime asOfUtc, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(bands);
        var from = asOfUtc - freshFor;
        var seen = new HashSet<(string, string)>();
        var byZone = new SortedDictionary<string, List<OverflowBandViewModel>>(StringComparer.Ordinal);
        foreach (var band in bands)
        {
            if (band?.Zone is not { Length: > 0 } zone || band.Band is not { Length: > 0 } name || !seen.Add((zone, name)))
                continue;
            var fresh = band.MinuteUtc is { } minute && minute >= from && minute <= asOfUtc;
            var occupancy = fresh ? band.MaxOccupancy : null;
            var state = Of(occupancy);
            var shown = string.Equals(state, NotHeard, StringComparison.Ordinal)
                ? new OverflowBandViewModel(name, state, null, null)
                : new OverflowBandViewModel(name, state, band.MinuteUtc, occupancy);
            if (!byZone.TryGetValue(zone, out var list))
                byZone[zone] = list = [];
            list.Add(shown);
        }

        return byZone.Select(z =>
        {
            var ordered = z.Value.OrderBy(b => b.Band, StringComparer.Ordinal).ToList();
            return new OverflowZoneViewModel(z.Key, OfZone(ordered.Select(b => b.State).ToList()), ordered);
        }).ToList();
    }
}

/// <summary>
/// The overflow bands of a site's queue zones (ARV-120) for the live and immigration screens (<c>LiveQueue.View</c>), limited
/// to the caller's sites: a site the caller does not reach answers like one that does not exist.
/// </summary>
public interface ISvcOverflowBands : ISvcScoped
{
    /// <summary>
    /// How far back a band's latest reading may lie to count (Proposed): the stream calls a band Unknown three minutes after
    /// its last reading, and a minute reaches the table about two and a half minutes after it starts (its minute, then the
    /// 90-second lateness), so 6 minutes on Ariva's clock.
    /// </summary>
    const int FreshMinutes = 6;

    /// <summary>At most this many bands in one answer; beyond it the read is refused (a profile holds at most 500 zones).</summary>
    const int MaxBands = 500;

    Task<Result<OverflowBandsViewModel>> GetAsync(string siteCode, CancellationToken ct = default);
}
