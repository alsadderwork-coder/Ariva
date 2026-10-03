using System.Text.RegularExpressions;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Flights;

namespace Ariva.Core.Border;

/// <summary>The outcome of one immigration record: applied (stored), or why not; its key is the desk, gate or flight it is about.</summary>
public sealed record ImmigrationItemResult(int Index, string Key, bool Applied, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>The answer to an immigration batch: records received, applied, unchanged (already received) and refused, each in order.</summary>
public sealed record ImmigrationBatchViewModel(int Received, int Applied, int Unchanged, int Refused, IReadOnlyList<ImmigrationItemResult> Items)
{
    public static ImmigrationBatchViewModel Of(IReadOnlyList<ImmigrationItemResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var refused = items.Count(i => i.HasErrors);
        var applied = items.Count(i => i.Applied);
        return new ImmigrationBatchViewModel(items.Count, applied, items.Count - applied - refused, refused, items);
    }
}

/// <summary>
/// What an immigration record from outside must be before Ariva keeps it (ARV-048, CWE-501): the four AMAN feed
/// contracts V1, whichever transport brought them. Codes are a border system's desk or gate codes (the desk code mapping
/// shape); a source event id is 1 to 64 letters, digits or <c>. _ : -</c>; intervals are exactly 60 seconds and start on
/// a whole minute; times lie between 7 days ago and <see cref="Ahead"/> from now; counts add up (accepted and rejected
/// to attempts, rejects by category to rejected, transactions at most documents); a reject category other than Other
/// holds 0 or at least 3 (AMAN's small-cell suppression); lanes are CIT, RES, VIS or CRW, empty exactly when a desk
/// closes; a flight's lanes and e-gate eligible are at most its boarded total. Reasons are Ariva's own and never quote a
/// value.
/// </summary>
public static partial class ImmigrationRules
{
    public const int IntervalSeconds = 60;
    public const int MaxCount = 10_000;
    public const int MaxPassengers = 1_000;
    public const double MaxSeconds = 3_600;
    public static readonly TimeSpan Behind = TimeSpan.FromDays(7);

    /// <summary>The lane categories of manual desks (AMAN codes, Ariva's lane categories).</summary>
    public static readonly IReadOnlySet<string> Lanes = new HashSet<string>(["CIT", "RES", "VIS", "CRW"], StringComparer.Ordinal);

    public static bool IsSourceEventId(string id) => id is not null && SourceEvent().IsMatch(id);

    public static IReadOnlyList<string> Check(DeskSessionChanged item, DateTime now, TimeSpan ahead)
    {
        var errors = new List<string>();
        if (item is null)
            return ["The item is empty."];
        Common(item.SiteCode, item.DeskCode, "deskCode", item.SourceEventId, errors);
        Time(item.OccurredAtUtc, now, ahead, "occurredAtUtc", errors, wholeMinute: false);
        if (!Enum.IsDefined(item.State))
            errors.Add("state is Opened, Closed or Paused.");
        else if (item.State == DeskSessionState.Closed ? !string.IsNullOrEmpty(item.LaneCategory) : item.LaneCategory is null || !Lanes.Contains(item.LaneCategory))
            errors.Add("laneCategory is CIT, RES, VIS or CRW for an open or paused desk, and empty when it closes.");
        return errors;
    }

    public static IReadOnlyList<string> Check(DeskIntervalStats item, DateTime now, TimeSpan ahead)
    {
        var errors = new List<string>();
        if (item is null)
            return ["The item is empty."];
        Common(item.SiteCode, item.DeskCode, "deskCode", item.SourceEventId, errors);
        Interval(item.IntervalStartUtc, item.IntervalSeconds, now, ahead, errors);
        if (item.TransactionsProcessed is < 0 or > MaxCount || item.DocumentsProcessed is < 0 or > MaxCount)
            errors.Add($"transactionsProcessed and documentsProcessed are 0 to {MaxCount}.");
        else if (item.TransactionsProcessed > item.DocumentsProcessed || (item.DocumentsProcessed == 0) != (item.TransactionsProcessed == 0))
            errors.Add("transactionsProcessed is at most documentsProcessed, and both are 0 or neither is (sums do not add up).");
        if (!Seconds(item.MeanServiceSeconds) || !Seconds(item.P90ServiceSeconds) || !Seconds(item.MeanCycleSeconds))
            errors.Add($"meanServiceSeconds, p90ServiceSeconds and meanCycleSeconds are 0 to {MaxSeconds:0}.");
        if (item.LaneCategory is null || !Lanes.Contains(item.LaneCategory))
            errors.Add("laneCategory is CIT, RES, VIS or CRW.");
        return errors;
    }

    public static IReadOnlyList<string> Check(EGateIntervalStats item, DateTime now, TimeSpan ahead)
    {
        var errors = new List<string>();
        if (item is null)
            return ["The item is empty."];
        Common(item.SiteCode, item.GateCode, "gateCode", item.SourceEventId, errors);
        Interval(item.IntervalStartUtc, item.IntervalSeconds, now, ahead, errors);
        if (item.Attempts is < 0 or > MaxCount || item.Accepted is < 0 or > MaxCount || item.Rejected is < 0 or > MaxCount || item.Accepted + item.Rejected != item.Attempts)
            errors.Add($"attempts is 0 to {MaxCount} and accepted and rejected add up to it.");
        var categories = item.RejectsByCategory ?? new Dictionary<EGateRejectCategory, int>();
        // Each count is bounded before the sum, so the sum of the (at most six) categories cannot overflow.
        if (categories.Keys.Any(k => !Enum.IsDefined(k)) || categories.Values.Any(v => v is < 0 or > MaxCount))
            errors.Add($"rejectsByCategory has only the coarse categories, each with a count of 0 to {MaxCount}.");
        else if (categories.Values.Sum() != item.Rejected)
            errors.Add("rejectsByCategory adds up to rejected.");
        else if (categories.Any(c => c.Key != EGateRejectCategory.Other && c.Value is > 0 and < 3))
            errors.Add("A reject category other than Other holds 0 or at least 3 (small-cell suppression).");
        if (!Seconds(item.MeanCycleSeconds))
            errors.Add($"meanCycleSeconds is 0 to {MaxSeconds:0}.");
        return errors;
    }

    public static IReadOnlyList<string> Check(InboundFlightLaneDemand item, DateTime now, TimeSpan ahead)
    {
        var errors = new List<string>();
        if (item is null)
            return ["The item is empty."];
        if (!IsSiteCode(item.SiteCode))
            errors.Add("siteCode is a site code.");
        if (FlightRules.NormalizeKey(item.FlightKey) is null)
            errors.Add("flightKey is 1 to 64 letters, digits or . _ : - characters.");
        if (!IsSourceEventId(item.SourceEventId))
            errors.Add("sourceEventId is 1 to 64 letters, digits or . _ : - characters.");
        var scheduled = item.ScheduledArrivalUtc.UtcDateTime;
        if (scheduled < now - FlightRules.ScheduledBack || scheduled > now + FlightRules.ScheduledAhead)
            errors.Add("scheduledArrivalUtc is at most 3 days ago and 400 days ahead.");
        Time(item.ComputedAtUtc, now, ahead, "computedAtUtc", errors, wholeMinute: false);
        var lanes = item.PassengersByLane ?? new Dictionary<string, int>();
        if (lanes.Keys.Any(k => k is null || !Lanes.Contains(k)) || lanes.Values.Any(v => v is < 0 or > MaxPassengers))
            errors.Add($"passengersByLane has the lanes CIT, RES, VIS and CRW, each 0 to {MaxPassengers}.");
        else if (item.BoardedTotal is < 0 or > MaxPassengers || item.EGateEligible is < 0 or > MaxPassengers)
            errors.Add($"boardedTotal and eGateEligible are 0 to {MaxPassengers}.");
        else if (lanes.Values.Sum() + item.EGateEligible > item.BoardedTotal)
            errors.Add("The lanes and the e-gate eligible add up to at most boardedTotal.");
        return errors;
    }

    private static void Common(string siteCode, string code, string name, string sourceEventId, List<string> errors)
    {
        if (!IsSiteCode(siteCode))
            errors.Add("siteCode is a site code.");
        if (DeskCodeMapping.NormalizeCode(code) is null || code != DeskCodeMapping.NormalizeCode(code))
            errors.Add($"{name} is 1 to 32 upper case letters, digits or . _ / - characters.");
        if (!IsSourceEventId(sourceEventId))
            errors.Add("sourceEventId is 1 to 64 letters, digits or . _ : - characters.");
    }

    private static void Interval(DateTimeOffset start, int seconds, DateTime now, TimeSpan ahead, List<string> errors)
    {
        if (seconds != IntervalSeconds)
            errors.Add("intervalSeconds is 60: V1 intervals are one minute.");
        Time(start, now, ahead, "intervalStartUtc", errors, wholeMinute: true);
    }

    private static void Time(DateTimeOffset value, DateTime now, TimeSpan ahead, string name, List<string> errors, bool wholeMinute)
    {
        var utc = value.UtcDateTime;
        if (utc < now - Behind || utc > now + ahead)
            errors.Add($"{name} is at most 7 days ago and not later than Ariva's clock allows.");
        else if (wholeMinute && utc.Ticks % TimeSpan.TicksPerMinute != 0)
            errors.Add($"{name} starts on a whole minute.");
    }

    private static bool Seconds(double value) => double.IsFinite(value) && value is >= 0 and <= MaxSeconds;

    public static bool IsSiteCode(string code) => code is not null && Site.IsValidCode(code);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SourceEvent();
}
