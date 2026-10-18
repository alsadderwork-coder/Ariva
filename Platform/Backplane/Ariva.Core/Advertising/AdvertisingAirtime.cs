using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Advertising;

/// <summary>
/// The airtime a display's plans gave each advertisement (ARV-144), the denominator of the share delivered: over each stretch
/// of the display's day where a playlist applied, each playable item takes its weight times its slot of every cycle (ARV-143b
/// point (1)), so its scheduled airtime is the stretch's length times that share of the cycle. Slides take their share too
/// (they fill the cycle) but are not advertisements, so they are not counted.
/// </summary>
public static class AdvertisingAirtime
{
    /// <summary>
    /// Per advertisement, the milliseconds the stretches scheduled for it. <paramref name="playable"/> gives a playlist's items
    /// that could play that day (an empty list when none could, or the playlist is unknown).
    /// </summary>
    public static IReadOnlyDictionary<Guid, double> Scheduled(IEnumerable<AdvertisingSchedules.Segment> segments,
        Func<Guid, IReadOnlyList<AdvertisingPlaylistItem>> playable)
    {
        ArgumentNullException.ThrowIfNull(playable);
        var scheduled = new Dictionary<Guid, double>();
        foreach (var segment in segments ?? [])
        {
            if (segment.Schedule is null)
                continue;
            var items = playable(segment.Schedule.PlaylistId) ?? [];
            var cycle = items.Sum(i => (double)i.Weight * i.Seconds);
            if (cycle <= 0)
                continue;
            var length = (segment.ToUtc - segment.FromUtc).TotalMilliseconds;
            foreach (var item in items.Where(i => i.Kind == AdvertisingItemKind.Media))
                scheduled[item.Id] = scheduled.GetValueOrDefault(item.Id) + length * item.Weight * item.Seconds / cycle;
        }

        return scheduled;
    }

    /// <summary>The share of its scheduled airtime an advertisement was on screen, or null when none was scheduled.</summary>
    public static double? Delivered(double airtimeMilliseconds, double scheduledMilliseconds) =>
        scheduledMilliseconds > 0 ? Math.Round(airtimeMilliseconds / scheduledMilliseconds, 4) : null;
}
