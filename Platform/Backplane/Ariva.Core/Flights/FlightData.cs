using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Flights;

/// <summary>
/// A flight leg as a feed reports it (ARV-041; wiki 08 <c>FlightLeg</c>), already parsed from the feed's format by its
/// adapter (the Integration API, AIDX, ACRIS, SSIM). Data from outside: <see cref="FlightRules.Check(FlightLegData, DateTime)"/>
/// decides whether it may touch the model (CWE-501). <see cref="Status"/> is a <see cref="FlightStatus"/> name; only
/// Cancelled and Diverted carry meaning of their own, the rest follows from the times.
/// </summary>
public sealed record FlightLegData(
    string FlightKey,
    string Carrier,
    string Number,
    string Suffix,
    string Direction,
    DateTime? ScheduledUtc,
    DateTime? EstimatedUtc = null,
    DateTime? ActualUtc = null,
    DateTime? OnBlockUtc = null,
    DateTime? OffBlockUtc = null,
    string Origin = null,
    string Destination = null,
    string Terminal = null,
    string Stand = null,
    string Gate = null,
    string AircraftType = null,
    int? Seats = null,
    int? PaxEstimate = null,
    string Status = null,
    IReadOnlyList<string> Codeshares = null);

/// <summary>A milestone of a known flight leg (wiki 08 <c>FlightEvent</c>): a <see cref="FlightEventType"/> name and when it happened.</summary>
public sealed record FlightEventData(string FlightKey, string EventType, DateTime? TimeUtc);

/// <summary>
/// The check-in counters a departing flight leg gets at a check-in checkpoint (wiki 08 <c>CounterAllocation</c>). The
/// counter codes are the AODB's; they resolve to Ariva desks through the desk code mappings (ARV-015).
/// </summary>
public sealed record CounterAllocationData(
    string FlightKey,
    string CheckpointCode,
    IReadOnlyList<string> CounterCodes,
    DateTime? OpenUtc,
    DateTime? CloseUtc,
    string HandlerCode = null);

/// <summary>The outcome of one item of a feed batch: applied (it changed something), or why not.</summary>
public sealed record FlightItemResult(int Index, string FlightKey, bool Applied, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// The rules feed data must meet before it reaches the flight model (ARV-041, CWE-501): every code has its shape,
/// every time is UTC and within the window a real feed can mean, every count is bounded, and names are exact. Values
/// come back normalised (trimmed, upper case) so the model stores one spelling.
/// </summary>
public static partial class FlightRules
{
    public const int MaxBatch = 500;
    public const int MaxCodeshares = 20;
    public const int MaxCounters = 100;
    public const int MaxSeats = 1_000;

    /// <summary>How far back a scheduled time may be (late messages for flights of the last days) and ahead (a season's schedule).</summary>
    public static readonly TimeSpan ScheduledBack = TimeSpan.FromDays(3);
    public static readonly TimeSpan ScheduledAhead = TimeSpan.FromDays(400);

    /// <summary>How far an estimate or actual may be from the schedule: early by a day, late by three (long delays).</summary>
    public static readonly TimeSpan EarlyBy = TimeSpan.FromDays(1);
    public static readonly TimeSpan LateBy = TimeSpan.FromDays(3);

    /// <summary>A message timestamp may be this far ahead of Ariva's clock (the feed's clock) and this far behind (a backlog).</summary>
    public static readonly TimeSpan SourceAhead = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SourceBack = TimeSpan.FromDays(30);

    /// <summary>Counters stay open at most this long.</summary>
    public static readonly TimeSpan MaxCounterOpen = TimeSpan.FromHours(24);

    public static string NormalizeKey(string key)
    {
        var trimmed = key?.Trim();
        return trimmed is not null && Key().IsMatch(trimmed) ? trimmed : null;
    }

    public static string NormalizeFeed(string feed)
    {
        var trimmed = feed?.Trim().ToLowerInvariant();
        return trimmed is not null && Feed().IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>A message timestamp Ariva can order by, or null.</summary>
    public static bool IsPlausibleSource(DateTime sourceUtc, DateTime now) =>
        sourceUtc.Kind == DateTimeKind.Utc && sourceUtc <= now + SourceAhead && sourceUtc >= now - SourceBack;

    /// <summary>The leg, normalised, or the reasons it cannot be taken.</summary>
    public static (FlightLegValues Values, IReadOnlyList<string> Errors) Check(FlightLegData data, DateTime now)
    {
        var errors = new List<string>();
        if (data is null)
            return (null, ["The item is empty."]);
        var key = NormalizeKey(data.FlightKey);
        if (key is null)
            errors.Add("flightKey is 1 to 64 letters, digits or . _ : - characters.");
        var carrier = Upper(data.Carrier);
        if (carrier is null || !Carrier().IsMatch(carrier))
            errors.Add("carrier is a two-character IATA or three-letter ICAO airline designator.");
        var number = data.Number?.Trim();
        if (number is null || !FlightNumber().IsMatch(number))
            errors.Add("number is 1 to 4 digits.");
        var suffix = Upper(data.Suffix);
        if (suffix is not null && !Suffix().IsMatch(suffix))
            errors.Add("suffix is one letter.");
        if (!Exact(data.Direction, out FlightDirection direction))
            errors.Add("direction is Arrival or Departure.");
        FlightStatus? status = null;
        if (data.Status is not null)
        {
            if (Exact(data.Status, out FlightStatus parsed))
                status = parsed;
            else
                errors.Add("status is a flight status name.");
        }

        var scheduleOk = false;
        if (data.ScheduledUtc is not { } scheduled)
        {
            errors.Add("scheduledUtc is required.");
            scheduled = default;
        }
        else if (scheduled.Kind != DateTimeKind.Utc || scheduled < now - ScheduledBack || scheduled > now + ScheduledAhead)
        {
            errors.Add("scheduledUtc is in UTC, at most 3 days ago and 400 days ahead.");
        }
        else
        {
            scheduleOk = true;
        }

        foreach (var (name, value) in new[] { ("estimatedUtc", data.EstimatedUtc), ("actualUtc", data.ActualUtc), ("onBlockUtc", data.OnBlockUtc), ("offBlockUtc", data.OffBlockUtc) })
        {
            // Only against a schedule that passed: any time a feed sends, however extreme, is an error, never an exception.
            if (value is { } time && scheduleOk && !NearSchedule(time, scheduled))
                errors.Add($"{name} is in UTC, at most a day before and 3 days after scheduledUtc.");
        }

        if (direction == FlightDirection.Arrival && data.OffBlockUtc is not null)
            errors.Add("offBlockUtc is for departures.");
        if (direction == FlightDirection.Departure && data.OnBlockUtc is not null)
            errors.Add("onBlockUtc is for arrivals.");
        var origin = Airport(data.Origin, "origin", errors);
        var destination = Airport(data.Destination, "destination", errors);
        var terminal = Place(data.Terminal, "terminal", errors);
        var stand = Place(data.Stand, "stand", errors);
        var gate = Place(data.Gate, "gate", errors);
        var aircraft = Upper(data.AircraftType);
        if (aircraft is not null && !AircraftType().IsMatch(aircraft))
            errors.Add("aircraftType is 2 to 4 letters or digits (IATA or ICAO type).");
        if (data.Seats is < 0 or > MaxSeats || data.PaxEstimate is < 0 or > MaxSeats)
            errors.Add("seats and paxEstimate are 0 to 1,000.");
        var codeshares = new List<string>();
        if (data.Codeshares is { } shares)
        {
            if (shares.Count > MaxCodeshares)
                errors.Add("At most 20 codeshares.");
            foreach (var share in shares.Take(MaxCodeshares))
            {
                var code = Upper(share);
                if (code is null || !Codeshare().IsMatch(code))
                    errors.Add("A codeshare is an airline designator and a flight number, such as XR1214.");
                else if (!System.Linq.Enumerable.Contains(codeshares, code, StringComparer.Ordinal))
                    codeshares.Add(code);
            }
        }

        if (errors.Count > 0)
            return (null, errors);
        return (new FlightLegValues(key, carrier, number, suffix, direction, Micro(scheduled), Micro(data.EstimatedUtc), Micro(data.ActualUtc), Micro(data.OnBlockUtc),
            Micro(data.OffBlockUtc),
            origin, destination, terminal, stand, gate, aircraft, data.Seats, data.PaxEstimate, status, codeshares), errors);
    }

    /// <summary>The event, parsed, or the reasons it cannot be taken. Which direction it fits is the flight's to say.</summary>
    public static (string Key, FlightEventType Type, DateTime TimeUtc, IReadOnlyList<string> Errors) Check(FlightEventData data)
    {
        var errors = new List<string>();
        var key = NormalizeKey(data?.FlightKey);
        if (key is null)
            errors.Add("flightKey is 1 to 64 letters, digits or . _ : - characters.");
        if (!Exact(data?.EventType, out FlightEventType type))
            errors.Add("eventType is Estimated, Landed, OnBlock, GateOpen, BoardingStart, OffBlock, Cancelled or Diverted.");
        if (data?.TimeUtc is not { Kind: DateTimeKind.Utc } time)
        {
            errors.Add("timeUtc is required, in UTC.");
            time = default;
        }

        return (key, type, Micro(time), errors);
    }

    /// <summary>The allocation, normalised, or the reasons it cannot be taken.</summary>
    public static (CounterAllocationValues Values, IReadOnlyList<string> Errors) Check(CounterAllocationData data)
    {
        var errors = new List<string>();
        var key = NormalizeKey(data?.FlightKey);
        if (key is null)
            errors.Add("flightKey is 1 to 64 letters, digits or . _ : - characters.");
        var checkpoint = Upper(data?.CheckpointCode);
        if (!Ariva.Core.Domain.Entities.TopologyCodes.IsValid(checkpoint))
            errors.Add("checkpointCode is a checkpoint code.");
        var counters = new List<string>();
        if (data?.CounterCodes is not { Count: > 0 } codes || codes.Count > MaxCounters)
        {
            errors.Add("counterCodes has 1 to 100 codes.");
        }
        else
        {
            foreach (var code in codes)
            {
                var normalized = Ariva.Core.Domain.Entities.DeskCodeMapping.NormalizeCode(code);
                if (normalized is null)
                    errors.Add("A counter code is 1 to 32 letters, digits or . _ / - characters.");
                else if (!System.Linq.Enumerable.Contains(counters, normalized, StringComparer.Ordinal))
                    counters.Add(normalized);
            }
        }

        if (data?.OpenUtc is not { Kind: DateTimeKind.Utc } open || data.CloseUtc is not { Kind: DateTimeKind.Utc } close || close <= open || close - open > MaxCounterOpen)
        {
            errors.Add("openUtc and closeUtc are in UTC, close after open, at most 24 hours apart.");
            open = close = default;
        }

        var handler = Upper(data?.HandlerCode);
        if (handler is not null && !Handler().IsMatch(handler))
            errors.Add("handlerCode is 2 to 8 letters or digits.");
        return errors.Count > 0 ? (null, errors) : (new CounterAllocationValues(key, checkpoint, counters, Micro(open), Micro(close), handler), errors);
    }

    // A difference of two DateTimes never overflows; scheduled minus a day could, near DateTime.MinValue.
    private static bool NearSchedule(DateTime time, DateTime scheduled)
    {
        var offset = time - scheduled;
        return time.Kind == DateTimeKind.Utc && offset >= -EarlyBy && offset <= LateBy;
    }

    /// <summary>
    /// A time as PostgreSQL keeps it (microseconds): a value read back equals the value applied, so a repeated message
    /// is seen as the same and changes nothing.
    /// </summary>
    public static DateTime Micro(DateTime value) => new(value.Ticks - value.Ticks % 10, value.Kind);

    public static DateTime? Micro(DateTime? value) => value is { } v ? Micro(v) : null;

    // Exact names only: "arrival", " Arrival", "1" or "Arrival,Departure" are refused (no Enum.TryParse leniency).
    private static bool Exact<T>(string value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        if (value is null || !Enum.GetNames<T>().Contains(value, StringComparer.Ordinal))
            return false;
        parsed = Enum.Parse<T>(value);
        return true;
    }

    private static string Upper(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    private static string Airport(string value, string name, List<string> errors)
    {
        var code = Upper(value);
        if (code is not null && !Ariva.Core.Domain.Entities.TopologyCodes.IsIata(code))
            errors.Add($"{name} is a three-letter IATA airport code.");
        return code;
    }

    private static string Place(string value, string name, List<string> errors)
    {
        var code = Upper(value);
        if (code is not null && !PlaceCode().IsMatch(code))
            errors.Add($"{name} is 1 to 16 letters, digits or . _ / - characters.");
        return code;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Key();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,31}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Feed();

    [GeneratedRegex(@"^([A-Z0-9]{2}|[A-Z]{3})\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Carrier();

    [GeneratedRegex(@"^[0-9]{1,4}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex FlightNumber();

    [GeneratedRegex(@"^[A-Z]\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Suffix();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._/-]{0,15}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PlaceCode();

    [GeneratedRegex(@"^[A-Z0-9]{2,4}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AircraftType();

    [GeneratedRegex(@"^([A-Z0-9]{2}|[A-Z]{3})[0-9]{1,4}[A-Z]?\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Codeshare();

    [GeneratedRegex(@"^[A-Z0-9]{2,8}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Handler();
}

/// <summary>A flight leg that passed <see cref="FlightRules"/>, normalised.</summary>
public sealed record FlightLegValues(
    string FlightKey,
    string Carrier,
    string Number,
    string Suffix,
    FlightDirection Direction,
    DateTime ScheduledUtc,
    DateTime? EstimatedUtc,
    DateTime? ActualUtc,
    DateTime? OnBlockUtc,
    DateTime? OffBlockUtc,
    string Origin,
    string Destination,
    string Terminal,
    string Stand,
    string Gate,
    string AircraftType,
    int? Seats,
    int? PaxEstimate,
    FlightStatus? Status,
    IReadOnlyList<string> Codeshares);

/// <summary>An allocation that passed <see cref="FlightRules"/>, normalised.</summary>
public sealed record CounterAllocationValues(string FlightKey, string CheckpointCode, IReadOnlyList<string> CounterCodes, DateTime OpenUtc, DateTime CloseUtc, string HandlerCode);

/// <summary>
/// The stale-feed rule (ARV-041, runbook 4.3): a feed is Fresh while its last message is within its cadence; past it,
/// Stale when flights are due at the site (silence is then a fault), Idle when none are (silence is expected).
/// </summary>
public static class FeedFreshnessRule
{
    public static FeedState Evaluate(DateTime? lastMessageUtc, DateTime now, TimeSpan staleAfter, int flightsDue)
    {
        if (staleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter), "A feed's cadence is positive.");
        if (lastMessageUtc is { } last && now - last <= staleAfter)
            return FeedState.Fresh;
        return flightsDue > 0 ? FeedState.Stale : FeedState.Idle;
    }
}
