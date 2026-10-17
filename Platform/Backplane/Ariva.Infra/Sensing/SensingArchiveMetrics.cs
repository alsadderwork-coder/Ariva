using System.Diagnostics.Metrics;

namespace Ariva.Infra.Sensing;

/// <summary>
/// Archive metrics (meter <c>Ariva.Sensing</c>, ARV-159): the sensing batches, sensing events and device health reports
/// the archive skipped. A skipped item is acknowledged and not archived (never dead-lettered or retried), so without this
/// count it shows only as a Warning line. One counter, <c>ariva.sensing.archive_skipped</c>, with a <c>kind</c> of
/// <see cref="BatchKind"/>, <see cref="EventKind"/> or <see cref="HealthReportKind"/>; no tag comes from the skipped
/// data (it failed the checks, so its site or zone cannot be trusted as a label).
/// </summary>
public sealed class SensingArchiveMetrics : IDisposable
{
    public const string MeterName = "Ariva.Sensing";
    public const string SkippedName = "ariva.sensing.archive_skipped";

    public const string BatchKind = "sensing_batch";
    public const string EventKind = "sensing_event";
    public const string HealthReportKind = "health_report";

    #region Fields

    private readonly Meter _meter;
    private readonly Counter<long> _skipped;

    #endregion

    #region Constructors

    public SensingArchiveMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _skipped = _meter.CreateCounter<long>(SkippedName,
            description: "Sensing batches, sensing events and device health reports the archive skipped as out of bounds (acknowledged, not archived)");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>Counts <paramref name="count"/> skipped items of one kind; nothing when the count is zero.</summary>
    public void Skipped(string kind, int count)
    {
        if (count > 0)
            _skipped.Add(count, new KeyValuePair<string, object>("kind", kind));
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
