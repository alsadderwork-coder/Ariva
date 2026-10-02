using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Sensing;

/// <summary>
/// The canonical sensing events every adapter emits, whatever the vendor sends (wiki/09, ADR-0003). Positions are floor
/// coordinates of the device's level in metres; times are the sensor's UTC clock. Track ids are ephemeral and are
/// namespaced by device before they leave the ingest, so two sensors' track 7 never meet (ADR-0011: no
/// re-identification). Lines and zones are named as in the site's zone profile.
/// </summary>
public abstract record CanonicalEvent(DateTime TimeUtc);

/// <summary>A track sample at 2 to 5 Hz (T3); stored as TrackSample.</summary>
public sealed record TrackPosition(string TrackId, double X, double Y, double? HeightMetres, DateTime TimeUtc) : CanonicalEvent(TimeUtc);

/// <summary>A person crossed a line (T1 to T3). Ariva computes its own crossings from tracks; a vendor's is a cross-check.</summary>
public sealed record LineCrossing(string LineName, CrossingDirection Direction, string TrackId, DateTime TimeUtc) : CanonicalEvent(TimeUtc);

/// <summary>People inside a zone at a moment (T2).</summary>
public sealed record ZoneOccupancy(string ZoneName, int Count, DateTime TimeUtc) : CanonicalEvent(TimeUtc);

/// <summary>Crossings of a line counted over an interval (T1); <see cref="CanonicalEvent.TimeUtc"/> is the interval's end.</summary>
public sealed record IntervalCount(string LineName, int In, int Out, DateTime FromUtc, DateTime TimeUtc) : CanonicalEvent(TimeUtc);

/// <summary>The device's health every 10 to 30 seconds (all tiers); stored as DeviceHealth.</summary>
public sealed record DeviceStatus(bool Online, double? TemperatureCelsius, double? FrameRate, double? ClockOffsetMilliseconds, DateTime TimeUtc) : CanonicalEvent(TimeUtc);

/// <summary>
/// The bounds every canonical event is checked against before it is used (CWE-501): every payload is untrusted. The
/// limits are wide enough for any real sensor and narrow enough that a broken or hostile one cannot poison a zone.
/// </summary>
public static partial class CanonicalEventRules
{
    /// <summary>The largest level is 2,000 m (Level.MaxExtentMetres); a little outside is allowed for footprint margins.</summary>
    public const double MaxCoordinateMetres = 2_100;

    public const int MaxTrackIdLength = 64;
    public const int MaxNameLength = 200;
    public const int MaxOccupancy = 10_000;
    public const int MaxIntervalCount = 100_000;

    /// <summary>Interval counts cover at most a day; vendors send 1 to 15 minutes.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    /// <summary>A clock offset beyond a day is not an offset, it is a misconfigured device.</summary>
    public const double MaxClockOffsetMilliseconds = 86_400_000;

    /// <summary>The device's own track id made unique in the site: "&lt;device code&gt;/&lt;track id&gt;".</summary>
    public static string NamespacedTrackId(string deviceCode, string trackId) => $"{deviceCode}/{trackId}";

    /// <summary>What is wrong with the event, or an empty list.</summary>
    public static IReadOnlyList<string> Validate(CanonicalEvent value)
    {
        var problems = new List<string>();
        if (value is null)
        {
            problems.Add("The event is missing.");
            return problems;
        }

        if (value.TimeUtc.Kind != DateTimeKind.Utc || value.TimeUtc.Year < 2000)
            problems.Add("The time must be UTC.");

        switch (value)
        {
            case TrackPosition track:
                TrackId(track.TrackId, required: true, problems);
                Coordinate(track.X, "x", problems);
                Coordinate(track.Y, "y", problems);
                if (track.HeightMetres is { } height && (!double.IsFinite(height) || height is < 0 or > 3))
                    problems.Add("A person's height is from 0 to 3 m.");
                break;
            case LineCrossing crossing:
                Name(crossing.LineName, "line", problems);
                if (!Enum.IsDefined(crossing.Direction))
                    problems.Add("The direction is In or Out.");
                TrackId(crossing.TrackId, required: false, problems);
                break;
            case ZoneOccupancy occupancy:
                Name(occupancy.ZoneName, "zone", problems);
                if (occupancy.Count is < 0 or > MaxOccupancy)
                    problems.Add($"Occupancy is from 0 to {MaxOccupancy}.");
                break;
            case IntervalCount interval:
                Name(interval.LineName, "line", problems);
                if (interval.In is < 0 or > MaxIntervalCount || interval.Out is < 0 or > MaxIntervalCount)
                    problems.Add($"Interval counts are from 0 to {MaxIntervalCount}.");
                if (interval.FromUtc.Kind != DateTimeKind.Utc || interval.FromUtc >= interval.TimeUtc || interval.TimeUtc - interval.FromUtc > MaxInterval)
                    problems.Add("An interval starts in UTC before it ends and lasts at most a day.");
                break;
            case DeviceStatus status:
                if (status.TemperatureCelsius is { } t && (!double.IsFinite(t) || t is < -60 or > 150))
                    problems.Add("The temperature is from -60 to 150 degrees Celsius.");
                if (status.FrameRate is { } f && (!double.IsFinite(f) || f is < 0 or > 1_000))
                    problems.Add("The frame rate is from 0 to 1,000 per second.");
                if (status.ClockOffsetMilliseconds is { } o && (!double.IsFinite(o) || Math.Abs(o) > MaxClockOffsetMilliseconds))
                    problems.Add("The clock offset is at most a day either way.");
                break;
            default:
                problems.Add("Unknown event.");
                break;
        }

        return problems;
    }

    private static void TrackId(string id, bool required, List<string> problems)
    {
        if (id is null)
        {
            if (required)
                problems.Add("A track id is required.");
            return;
        }

        if (!TrackIdPattern().IsMatch(id))
            problems.Add($"A track id is 1 to {MaxTrackIdLength} letters, digits, dots, hyphens or underscores.");
    }

    private static void Name(string name, string what, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || !Domain.Components.DisplayText.IsClean(name))
            problems.Add($"A {what} name is 1 to {MaxNameLength} printable characters.");
    }

    private static void Coordinate(double value, string axis, List<string> problems)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxCoordinateMetres)
            problems.Add($"The {axis} coordinate is a number of metres within {MaxCoordinateMetres}.");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TrackIdPattern();
}
