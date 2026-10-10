using System.Globalization;
using System.Text.Json;

namespace Ariva.Core.Availability;

/// <summary>
/// One interval of operating hours in the site's local wall-clock time (ARV-118, formulas F18), belonging to the day it
/// opens on. <see cref="OpensMinute"/> is the minute of the day it opens (0 to 1,439); <see cref="LengthMinutes"/> is 1 to
/// 1,440, so an interval may run past midnight into the next day (18:00 to 02:00 opens on the first day and closes on the
/// second). Written as "HH:MM": opens 00:00 to 23:59, closes 00:00 to 23:59 or 24:00; a closing time at or before the
/// opening time (other than 24:00) is on the next day, and equal times are refused (00:00 to 24:00 is the whole day).
/// </summary>
public readonly record struct OperatingInterval
{
    public const int MinutesPerDay = 1_440;

    public OperatingInterval(int opensMinute, int lengthMinutes)
    {
        if (opensMinute is < 0 or >= MinutesPerDay)
            throw new ArgumentOutOfRangeException(nameof(opensMinute), "An interval opens at minute 0 to 1,439 of its day.");
        if (lengthMinutes is < 1 or > MinutesPerDay)
            throw new ArgumentOutOfRangeException(nameof(lengthMinutes), "An interval lasts 1 to 1,440 minutes.");
        OpensMinute = opensMinute;
        LengthMinutes = lengthMinutes;
    }

    public int OpensMinute { get; }
    public int LengthMinutes { get; }

    /// <summary>The minute it closes, counted from the start of the day it opens (above 1,440 when it runs past midnight).</summary>
    public int ClosesMinute => OpensMinute + LengthMinutes;

    /// <summary>True when it runs past midnight into the next day.</summary>
    public bool Overnight => ClosesMinute > MinutesPerDay;

    public string Opens => Format(OpensMinute);

    public string Closes => ClosesMinute == MinutesPerDay ? "24:00" : Format(ClosesMinute % MinutesPerDay);

    /// <summary>The interval from "HH:MM" texts, or false (nothing else is accepted: no seconds, no single digits).</summary>
    public static bool TryParse(string opens, string closes, out OperatingInterval interval)
    {
        interval = default;
        if (!TryMinute(opens, allowEndOfDay: false, out var open) || !TryMinute(closes, allowEndOfDay: true, out var close) || open == close)
            return false;
        var length = close > open ? close - open : close + MinutesPerDay - open;
        if (length is < 1 or > MinutesPerDay)
            return false;
        interval = new OperatingInterval(open, length);
        return true;
    }

    /// <summary>True when the minute of the day (0 to 1,439) of the day it opens lies inside it.</summary>
    public bool CoversSameDay(int minuteOfDay) => minuteOfDay >= OpensMinute && minuteOfDay < Math.Min(ClosesMinute, MinutesPerDay);

    /// <summary>True when the minute of the day (0 to 1,439) of the day after it opens lies inside it.</summary>
    public bool CoversNextDay(int minuteOfDay) => Overnight && minuteOfDay < ClosesMinute - MinutesPerDay;

    private static bool TryMinute(string text, bool allowEndOfDay, out int minute)
    {
        minute = -1;
        if (text is not { Length: 5 } || text[2] != ':' || !char.IsAsciiDigit(text[0]) || !char.IsAsciiDigit(text[1]) ||
            !char.IsAsciiDigit(text[3]) || !char.IsAsciiDigit(text[4]))
            return false;
        var hours = ((text[0] - '0') * 10) + (text[1] - '0');
        var minutes = ((text[3] - '0') * 10) + (text[4] - '0');
        if (allowEndOfDay && hours == 24 && minutes == 0)
        {
            minute = MinutesPerDay;
            return true;
        }

        if (hours > 23 || minutes > 59)
            return false;
        minute = (hours * 60) + minutes;
        return true;
    }

    private static string Format(int minute) => string.Create(CultureInfo.InvariantCulture, $"{minute / 60:00}:{minute % 60:00}");
}

/// <summary>An interval of a weekday, as requests, views and the stored JSON write it.</summary>
public sealed record OperatingDayHours(string Day, string Opens, string Closes);

/// <summary>An interval of one dated day (an exception), as requests, views and the stored JSON write it.</summary>
public sealed record OperatingHoursText(string Opens, string Closes);

/// <summary>
/// A site's weekly operating hours (ARV-118): up to <see cref="MaxIntervalsPerDay"/> intervals per weekday, none
/// overlapping another on the week's circle (Sunday's overnight interval runs into Monday). A day without intervals is
/// closed. Stored as canonical JSON (<see cref="ToJson"/>); stored text is read back through <see cref="TryFromJson"/>,
/// which refuses anything a request could not have produced (CWE-501).
/// </summary>
public sealed class WeeklyHours
{
    public const int MaxIntervalsPerDay = 4;
    public const int MaxJsonLength = 4_000;
    private const int MinutesPerWeek = 7 * OperatingInterval.MinutesPerDay;

    public const string InvalidDay = "A day is Monday, Tuesday, Wednesday, Thursday, Friday, Saturday or Sunday.";
    public const string InvalidInterval = "Hours are HH:MM, opening 00:00 to 23:59 and closing 00:00 to 24:00, not at the opening time; a closing time before the opening time is on the next day.";
    public const string TooManyIntervals = "A day has at most 4 intervals.";
    public const string Overlapping = "The intervals overlap; an overnight interval runs into the next day.";

    /// <summary>Monday first, as the calendar is shown.</summary>
    public static readonly IReadOnlyList<DayOfWeek> Days =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 4 };

    private readonly IReadOnlyList<OperatingInterval>[] _days;

    private WeeklyHours(IReadOnlyList<OperatingInterval>[] days) => _days = days;

    /// <summary>Every day 00:00 to 24:00: the hours of a site without a calendar, or before its first version takes effect.</summary>
    public static WeeklyHours AlwaysOpen { get; } = new([.. Enumerable.Range(0, 7).Select(_ => (IReadOnlyList<OperatingInterval>)[new OperatingInterval(0, OperatingInterval.MinutesPerDay)])]);

    public IReadOnlyList<OperatingInterval> On(DayOfWeek day) => _days[(int)day];

    /// <summary>The week from day and "HH:MM" texts, or the first problem found (never a value from the request).</summary>
    public static (WeeklyHours Hours, string Problem) Parse(IEnumerable<OperatingDayHours> intervals)
    {
        var days = Enumerable.Range(0, 7).Select(_ => new List<OperatingInterval>()).ToArray();
        foreach (var item in intervals ?? [])
        {
            if (item is null || !TryDay(item.Day, out var day))
                return (null, InvalidDay);
            if (!OperatingInterval.TryParse(item.Opens, item.Closes, out var interval))
                return (null, InvalidInterval);
            days[(int)day].Add(interval);
            if (days[(int)day].Count > MaxIntervalsPerDay)
                return (null, TooManyIntervals);
        }

        if (OverlapsOnTheWeek(days))
            return (null, Overlapping);
        return (new WeeklyHours([.. days.Select(d => (IReadOnlyList<OperatingInterval>)[.. d.OrderBy(i => i.OpensMinute)])]), null);
    }

    /// <summary>One day's intervals from "HH:MM" texts (a dated exception), or the first problem found.</summary>
    public static (IReadOnlyList<OperatingInterval> Hours, string Problem) ParseDay(IEnumerable<OperatingHoursText> intervals)
    {
        var day = new List<OperatingInterval>();
        foreach (var item in intervals ?? [])
        {
            if (item is null || !OperatingInterval.TryParse(item.Opens, item.Closes, out var interval))
                return (null, InvalidInterval);
            day.Add(interval);
            if (day.Count > MaxIntervalsPerDay)
                return (null, TooManyIntervals);
        }

        var ordered = day.OrderBy(i => i.OpensMinute).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].OpensMinute < ordered[i - 1].ClosesMinute)
                return (null, Overlapping);
        }

        return (ordered, null);
    }

    /// <summary>The intervals in day order (Monday first), as views and the stored JSON write them.</summary>
    public IReadOnlyList<OperatingDayHours> Intervals() =>
        [.. Days.SelectMany(d => On(d).Select(i => new OperatingDayHours(d.ToString(), i.Opens, i.Closes)))];

    public string ToJson() => JsonSerializer.Serialize(Intervals(), Json);

    /// <summary>The week from stored JSON; null when the text is not one <see cref="ToJson"/> could have written.</summary>
    public static WeeklyHours TryFromJson(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxJsonLength)
            return null;
        try
        {
            var items = JsonSerializer.Deserialize<List<OperatingDayHours>>(json, Json);
            return items is null || items.Count > 7 * MaxIntervalsPerDay ? null : Parse(items).Hours;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One day's intervals as JSON ([] for a closed day).</summary>
    public static string DayToJson(IReadOnlyList<OperatingInterval> hours) => JsonSerializer.Serialize(DayTexts(hours), Json);

    /// <summary>One day's intervals as texts, in opening order.</summary>
    public static IReadOnlyList<OperatingHoursText> DayTexts(IReadOnlyList<OperatingInterval> hours) => [.. (hours ?? []).Select(i => new OperatingHoursText(i.Opens, i.Closes))];

    /// <summary>One day's intervals from stored JSON; null when not valid.</summary>
    public static IReadOnlyList<OperatingInterval> TryDayFromJson(string json)
    {
        if (json is null || json.Length > MaxJsonLength)
            return null;
        try
        {
            var items = JsonSerializer.Deserialize<List<OperatingHoursText>>(json, Json);
            return items is null || items.Count > MaxIntervalsPerDay ? null : ParseDay(items).Hours;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A weekday by its English name, exactly (no numbers, no other case).</summary>
    public static bool TryDay(string text, out DayOfWeek day)
    {
        day = default;
        if (text is null || text.Length > 9)
            return false;
        foreach (var candidate in Days)
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
            {
                day = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool OverlapsOnTheWeek(List<OperatingInterval>[] days)
    {
        // Monday is minute 0 of the week; an interval past the week's end wraps to Monday.
        var spans = new List<(int Start, int End)>();
        foreach (var day in Days)
        {
            var offset = ((int)day + 6) % 7 * OperatingInterval.MinutesPerDay;
            foreach (var interval in days[(int)day])
            {
                var start = offset + interval.OpensMinute;
                var end = start + interval.LengthMinutes;
                if (end <= MinutesPerWeek)
                {
                    spans.Add((start, end));
                }
                else
                {
                    spans.Add((start, MinutesPerWeek));
                    spans.Add((0, end - MinutesPerWeek));
                }
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start < spans[i - 1].End)
                return true;
        }

        return false;
    }
}
