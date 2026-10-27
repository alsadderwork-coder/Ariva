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
/// unmapped codes per site. Exported since ARV-125a. Since ARV-257 also <see cref="FeedAgeName"/>, the seconds since each
/// border site's AMAN feed was last heard (<see cref="AmanFeedAges"/>), tagged with the site only, which the AMAN feed alert
/// reads per site (before, it watched <c>ariva.border.feed.records</c>, which names no site, so one site's feed could stop
/// unnoticed while another's flowed).
/// </summary>
public sealed class BorderMetrics : IDisposable
{
    public const string MeterName = "Ariva.Border";

    public const string FeedAgeName = "ariva.border.feed_age_seconds";

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

    /// <summary>
    /// Observes the AMAN feed's age per border site (ARV-257): <paramref name="ages"/> gives each site this host reports and
    /// the seconds since its feed was last heard, read when the gauge is collected. Called once, by <see cref="AmanFeedAges"/>.
    /// </summary>
    public void ObserveFeedAges(Func<IEnumerable<(string Site, double Seconds)>> ages)
    {
        ArgumentNullException.ThrowIfNull(ages);
        _meter.CreateObservableGauge(FeedAgeName,
            () => ages().Select(a => new Measurement<double>(a.Seconds, new KeyValuePair<string, object>("site", a.Site))),
            unit: "s",
            description: "Seconds since the border site's AMAN feed was last heard (the desk feed's heartbeat), reported by the Stream replica that read the site lately");
    }

    public void Dispose() => _meter.Dispose();
}
