using System.Diagnostics.Metrics;
using Ariva.Core.Services.Displays;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The passenger displays' metrics (meter <c>Ariva.Displays</c>, Ariva.Api.Main only). <see cref="StaleName"/> (ARV-259d) is, by
/// <c>site</c>, the enabled displays whose board would be stale now (a queue zone's live snapshot missing or older than the
/// display's stale threshold) at the last display stale sweep: every site with an enabled display the sweep judged, 0 when none
/// is stale, at most <see cref="MaxSites"/> (those with the most stale displays first, then by code). Only the replica that won
/// the sweep's lock computes it, and reports it for two sweep intervals, so copies overlap at a hand-over: a dashboard takes the
/// largest copy of each site, never the sum.
/// </summary>
public sealed class DisplayMetrics : IDisposable
{
    public const string MeterName = "Ariva.Displays";
    public const string StaleName = "ariva.displays.stale";

    /// <summary>The sites one reading carries at most.</summary>
    public const int MaxSites = 100;

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private volatile StaleState _stale;

    #endregion

    private sealed record StaleState(IReadOnlyList<(string Site, int Stale)> Sites, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public DisplayMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(StaleName, ObserveStale, unit: "{display}",
            description: "Enabled displays whose board would be stale now (a queue zone's live snapshot missing or older than the display's stale threshold), by site, at the last display stale sweep");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>
    /// The sites of a display stale sweep this replica just ran, reported for <paramref name="freshFor"/> (two sweep intervals): at
    /// most <see cref="MaxSites"/>, those with the most stale displays first, then by code.
    /// </summary>
    public void Stale(IReadOnlyList<DisplayStaleSite> sites, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(sites);
        _stale = new StaleState([.. sites.OrderByDescending(s => s.Stale).ThenBy(s => s.SiteCode, StringComparer.Ordinal).Take(MaxSites).Select(s => (s.SiteCode, s.Stale))],
            _time.GetUtcNow(), freshFor);
    }

    /// <summary>Nothing more to report (the sweep stopped).</summary>
    public void Clear() => _stale = null;

    private IEnumerable<Measurement<int>> ObserveStale()
    {
        var stale = _stale;
        if (stale is null || _time.GetUtcNow() - stale.At > stale.FreshFor)
            return [];
        return stale.Sites.Select(s => new Measurement<int>(s.Stale, new KeyValuePair<string, object>("site", s.Site)));
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
