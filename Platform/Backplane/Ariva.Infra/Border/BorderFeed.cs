using System.Diagnostics.Metrics;

namespace Ariva.Infra.Border;

/// <summary>
/// The immigration feed settings (<c>Border:Feed</c>, ARV-048): how far ahead of Ariva's clock a record's time may be.
/// AMAN publishes closed intervals, so 5 minutes allows for clock skew; a demonstration that plays the demo day faster
/// than real time (the simulator) needs more.
/// </summary>
public sealed record BorderFeedSettings
{
    public const string SectionName = "Border:Feed";

    public int AheadMinutes { get; init; } = 5;

    public TimeSpan Ahead => TimeSpan.FromMinutes(AheadMinutes);

    public IEnumerable<string> Problems()
    {
        if (AheadMinutes is < 1 or > 2880)
            yield return "Border:Feed:AheadMinutes is 1 to 2880.";
    }
}

/// <summary>
/// The immigration feed's metrics (meter Ariva.Border): records by contract, transport and outcome, and records of
/// unmapped codes per site. Exported since ARV-125a; the AMAN feed alert (ARV-125b) watches <c>ariva.border.feed.records</c>
/// whose outcome is not refused.
/// </summary>
public sealed class BorderMetrics : IDisposable
{
    public const string MeterName = "Ariva.Border";
    private readonly Meter _meter;
    private readonly Counter<long> _records;
    private readonly Counter<long> _unmapped;

    public BorderMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _records = _meter.CreateCounter<long>("ariva.border.feed.records", description: "Immigration records received, by contract, feed kind and outcome");
        _unmapped = _meter.CreateCounter<long>("ariva.border.feed.unmapped", description: "Immigration records whose desk or gate code has no AMAN desk code mapping");
    }

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    public void Record(string contract, string feedKind, string outcome) =>
        _records.Add(1, new("contract", contract), new("feed_kind", feedKind), new("outcome", outcome));

    public void Unmapped(string site) => _unmapped.Add(1, new KeyValuePair<string, object>("site", site));

    public void Dispose() => _meter.Dispose();
}
