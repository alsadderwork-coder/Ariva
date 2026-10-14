using System.Diagnostics.Metrics;

namespace Ariva.Infra.Messaging.Outbox;

/// <summary>What the outbox held unsent when it was measured.</summary>
/// <param name="Unsent">Rows not yet sent (events and kept dead letters).</param>
/// <param name="OldestUtc">When the oldest of them was written; null when there is none.</param>
public sealed record OutboxBacklog(long Unsent, DateTime? OldestUtc);

/// <summary>
/// The outbox's backlog (meter <c>Ariva.Outbox</c>, ARV-125a): <see cref="UnsentName"/>, the rows not yet sent, and
/// <see cref="OldestUnsentAgeName"/>, how old the oldest of them was when measured (0 when there is none). The table is
/// shared by every host, so every relay measures it (at most every <see cref="OutboxRelay.MeasureEvery"/>, outside its
/// pass and its leader lock) and they all report the same figures: a dashboard takes the largest, never the sum. A
/// measurement is reported for three intervals; a relay that cannot measure (the database away) reports nothing, which
/// the database readiness check already raises. No tags.
/// </summary>
public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "Ariva.Outbox";
    public const string UnsentName = "ariva.outbox.unsent";
    public const string OldestUnsentAgeName = "ariva.outbox.oldest_unsent_age";

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private volatile Snapshot _last;

    #endregion

    private sealed record Snapshot(long Unsent, double OldestAgeSeconds, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public OutboxMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(UnsentName, () => Fresh() is { } last ? [new Measurement<long>(last.Unsent)] : Array.Empty<Measurement<long>>(),
            unit: "{row}", description: "Outbox rows not yet sent to Kafka (events and kept dead letters)");
        _meter.CreateObservableGauge(OldestUnsentAgeName, () => Fresh() is { } last ? [new Measurement<double>(last.OldestAgeSeconds)] : Array.Empty<Measurement<double>>(),
            unit: "s", description: "Age of the oldest outbox row not yet sent when measured, 0 when there is none");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>Reports a measurement taken now, valid for <paramref name="freshFor"/>.</summary>
    public void Report(OutboxBacklog backlog, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(backlog);
        var now = _time.GetUtcNow();
        var age = backlog.OldestUtc is { } oldest ? Math.Max(0, (now.UtcDateTime - DateTime.SpecifyKind(oldest, DateTimeKind.Utc)).TotalSeconds) : 0;
        _last = new Snapshot(Math.Max(0, backlog.Unsent), age, now, freshFor);
    }

    private Snapshot Fresh()
    {
        var last = _last;
        return last is not null && _time.GetUtcNow() - last.At <= last.FreshFor ? last : null;
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
