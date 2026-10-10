using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>
/// The errors a rehearsal injects into what its observers report (ARV-104i), all systematic so that a rehearsal's outcome is
/// known in advance: a count error in percent (on every line, or only on the lines named), a tracer error in minutes and in
/// percent of the true wait, and observer slips: the share of count bins and desk bins an observer misses, and the share of
/// desk minutes left unobserved. Which bins and minutes slip is drawn from the rehearsal's seed, so the same seed misses the
/// same ones. With no error (<see cref="None"/>) the observers report the scenario's truth exactly.
/// </summary>
public sealed record ObserverErrors(
    double CountErrorPercent,
    IReadOnlyCollection<string> CountErrorLines,
    double TracerErrorMinutes,
    double TracerErrorPercent,
    double MissedBinsPercent,
    double MissedMinutesPercent)
{
    /// <summary>Count errors from -50 to +50 percent.</summary>
    public const double MaxCountErrorPercent = 50;

    /// <summary>Tracer errors from -30 to +30 minutes.</summary>
    public const double MaxTracerErrorMinutes = 30;

    /// <summary>Tracer errors from -50 to +50 percent.</summary>
    public const double MaxTracerErrorPercent = 50;

    /// <summary>The observers report the truth.</summary>
    public static readonly ObserverErrors None = new(0, null, 0, 0, 0, 0);

    /// <summary>Whether the count error applies to a line: every line when no line is named, otherwise the named ones (exact names).</summary>
    public bool CountErrorApplies(string lineName) =>
        CountErrorPercent != 0 && (CountErrorLines is null || CountErrorLines.Count == 0 || (lineName is not null && CountErrorLines.Contains(lineName)));

    /// <summary>
    /// A line name a rehearsal may name (and its report repeats): 1 to 200 letters, digits, spaces and <c>- _ . ( ) /</c>, as the
    /// demo profiles name lines ("A-VIS entry"). No markup, quote or control character is taken, so none is ever echoed; a line
    /// whose Ariva name has other characters takes the count error with every line (no line named).
    /// </summary>
    public static bool IsLineName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 200 &&
        name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' or '/');

    /// <summary>What is wrong with the errors, never quoting a line name.</summary>
    public IEnumerable<string> Problems()
    {
        if (!double.IsFinite(CountErrorPercent) || Math.Abs(CountErrorPercent) > MaxCountErrorPercent)
            yield return "countErrorPercent is from -50 to 50.";
        if (CountErrorLines is { Count: > 50 } || (CountErrorLines?.Any(l => !IsLineName(l)) ?? false))
            yield return "countErrorLines names at most 50 lines of the campaign, each 1 to 200 letters, digits, spaces or - _ . ( ) /.";
        if (!double.IsFinite(TracerErrorMinutes) || Math.Abs(TracerErrorMinutes) > MaxTracerErrorMinutes)
            yield return "tracerErrorMinutes is from -30 to 30.";
        if (!double.IsFinite(TracerErrorPercent) || Math.Abs(TracerErrorPercent) > MaxTracerErrorPercent)
            yield return "tracerErrorPercent is from -50 to 50.";
        if (!double.IsFinite(MissedBinsPercent) || MissedBinsPercent is < 0 or > 100)
            yield return "missedBinsPercent is from 0 to 100.";
        if (!double.IsFinite(MissedMinutesPercent) || MissedMinutesPercent is < 0 or > 100)
            yield return "missedMinutesPercent is from 0 to 100.";
    }
}

/// <summary>The error injection of a rehearsal (ARV-104i): pure and deterministic, table-tested.</summary>
internal static class ObserverSlips
{
    /// <summary>People crossing one line in 15 minutes, at most, as Ariva takes a manual count (ManualCount.MaxCrossings).</summary>
    public const int MaxCrossings = 10_000;

    /// <summary>A tracer run lasts at most 3 hours (TracerRun.MaxDuration).</summary>
    public static readonly TimeSpan MaxRun = TimeSpan.FromHours(3);

    /// <summary>The shortest run a rehearsal reports: Ariva refuses a run that lasts nothing.</summary>
    public static readonly TimeSpan MinRun = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The count an observer reports for <paramref name="truth"/> people with an error of <paramref name="percent"/>: the true
    /// count scaled and rounded half away from zero (106 for 100 at +6 percent, 11 for 10, 8 for 8), within 0 and
    /// <see cref="MaxCrossings"/>.
    /// </summary>
    public static int Count(int truth, double percent) =>
        (int)Math.Clamp(Math.Round(truth * (1 + (percent / 100)), MidpointRounding.AwayFromZero), 0, MaxCrossings);

    /// <summary>
    /// The wait a tracer reports for a true wait: scaled by <paramref name="percent"/>, then <paramref name="minutes"/> added, to the
    /// millisecond, within <see cref="MinRun"/> and <see cref="MaxRun"/>.
    /// </summary>
    public static TimeSpan TracerWait(TimeSpan truth, double minutes, double percent)
    {
        var milliseconds = Math.Round((truth.TotalMilliseconds * (1 + (percent / 100))) + (minutes * 60_000), MidpointRounding.AwayFromZero);
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, MinRun.TotalMilliseconds, MaxRun.TotalMilliseconds));
    }

    /// <summary>
    /// Whether an observer misses the item <paramref name="key"/> of a kind ("count", "desk-bin", "desk-minute") at
    /// <paramref name="percent"/> percent: a draw from the seed, the kind and the key, so the same seed misses the same items.
    /// </summary>
    public static bool Missed(uint seed, string kind, string key, double percent) =>
        percent > 0 && (percent >= 100 || ScenarioMath.H3(seed ^ ScenarioMath.StrHash(kind ?? string.Empty), unchecked((int)ScenarioMath.StrHash(key ?? string.Empty)), 0x5eed) < percent / 100);
}
