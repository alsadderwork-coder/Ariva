using System.Globalization;
using System.Text.Json;
using Ariva.Core.Sensing;

namespace Ariva.Infra.Sensing;

/// <summary>
/// A vendor payload mapped to canonical events (ARV-023), before validation: the device's own send time and package id
/// when the payload has them, the events, how many vendor items were ignored on purpose (types Ariva does not use) and
/// short notes on why. A connection test carries nothing.
/// </summary>
public sealed record MappedPush(
    DateTime? DeviceSentUtc,
    IReadOnlyList<long> PackageIds,
    IReadOnlyList<TrackPosition> Tracks,
    IReadOnlyList<LineCrossing> Crossings,
    IReadOnlyList<ZoneOccupancy> Occupancy,
    IReadOnlyList<IntervalCount> Intervals,
    DeviceStatus Status,
    int Ignored,
    IReadOnlyList<string> Notes,
    bool ConnectionTest = false)
{
    public int EventCount => Tracks.Count + Crossings.Count + Occupancy.Count + Intervals.Count + (Status is null ? 0 : 1);
}

/// <summary>The payload is not what the dialect expects (400); the message says what, never echoing the payload.</summary>
public sealed class PushFormatException(string message) : Exception(message);

/// <summary>Where the device hangs on its level, to bring sensor-local positions into floor coordinates.</summary>
public sealed record DevicePose(double X, double Y, double OrientationDegrees)
{
    /// <summary>A point given in the sensor's frame (metres, x along the sensor's orientation) on the level's floor.</summary>
    public (double X, double Y) ToFloor(double localX, double localY)
    {
        var angle = OrientationDegrees * Math.PI / 180;
        return (X + (localX * Math.Cos(angle)) - (localY * Math.Sin(angle)), Y + (localX * Math.Sin(angle)) + (localY * Math.Cos(angle)));
    }
}

/// <summary>Reading vendor JSON defensively: types are checked, never assumed; numbers must be finite.</summary>
internal static class PushJson
{
    /// <summary>
    /// Payload text fit to quote in an answer or a log: letters, digits, spaces and . _ - / only, at most 40 characters,
    /// in single quotes. Anything else becomes '?', so no markup, line break or control character is ever echoed.
    /// </summary>
    public static string Quote(string text)
    {
        if (text is null)
            return "'?'";
        var kept = text.Length <= 40 ? text : text[..40];
        var chars = kept.Select(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '/' ? c : '?').ToArray();
        return "'" + new string(chars) + (text.Length > 40 ? "..." : string.Empty) + "'";
    }

    /// <summary>A time as RFC 3339 with an explicit offset, or Unix time in milliseconds (or seconds below 10^11).</summary>
    public static DateTime Time(JsonElement value, string what)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                if (text is { Length: <= 40 } && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && HasOffset(text))
                    return parsed.UtcDateTime;
                throw new PushFormatException($"{what} is not an RFC 3339 time with an offset.");
            case JsonValueKind.Number when value.TryGetInt64(out var number) && number > 0:
                try
                {
                    return number >= 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new PushFormatException($"{what} is out of range.");
                }

            default:
                throw new PushFormatException($"{what} is not a time.");
        }
    }

    private static bool HasOffset(string text)
    {
        var t = text.IndexOf('T', StringComparison.Ordinal);
        if (t < 0)
            return false;
        var clock = text[(t + 1)..];
        return clock.EndsWith('Z') || clock.EndsWith('z') || clock.Contains('+', StringComparison.Ordinal) || clock.Contains('-', StringComparison.Ordinal);
    }

    public static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    public static JsonElement Required(JsonElement element, string name, JsonValueKind kind, string where)
    {
        if (!TryProperty(element, name, out var value) || value.ValueKind != kind)
            throw new PushFormatException($"{where}.{name} is missing or not {(kind == JsonValueKind.Array ? "an array" : kind == JsonValueKind.Object ? "an object" : kind.ToString().ToLowerInvariant())}.");
        return value;
    }

    public static string Text(JsonElement element, string name, int maxLength = 200)
    {
        if (!TryProperty(element, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return text is null || text.Length > maxLength ? null : text.Trim();
        }

        return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }

    public static double? Number(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number))
            return null;
        return number;
    }

    public static long? Long(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
