using System.Diagnostics.Metrics;

namespace Ariva.Infra.Sensing;

/// <summary>
/// Ingest metrics (meter <c>Ariva.Ingest</c>, ARV-277). One counter, <c>ariva.ingest.undeclared_line_events</c>: the crossings and
/// interval readings Ingest accepted on an entry or exit line of the device's queue zone that the device does not declare it counts
/// (a <c>Listed</c> or <c>None</c> declaration, ARV-259e4). They are still published and counted by the queue engine; the counter and
/// the push answer's problems are the warning (the owner's decision of 2026-10-29: warn, count and name, warn). Its one tag is
/// <c>site</c>, the authenticated device's site from the registry (never a value from the push), so its series are bounded by the
/// registered sites; no line or device name is a tag.
/// </summary>
public sealed class IngestMetrics : IDisposable
{
    public const string MeterName = "Ariva.Ingest";
    public const string UndeclaredName = "ariva.ingest.undeclared_line_events";

    #region Fields

    private readonly Meter _meter;
    private readonly Counter<long> _undeclared;

    #endregion

    #region Constructors

    public IngestMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _undeclared = _meter.CreateCounter<long>(UndeclaredName,
            description: "Crossings and interval readings accepted on an entry or exit line their device does not declare it counts (still published)");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>Counts <paramref name="count"/> events on undeclared lines at <paramref name="site"/>; nothing when the count is zero.</summary>
    public void Undeclared(string site, long count)
    {
        if (count > 0)
            _undeclared.Add(count, new KeyValuePair<string, object>("site", site));
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
