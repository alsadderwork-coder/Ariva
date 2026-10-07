namespace Ariva.Core.Desks;

/// <summary>A buffered desk signal in a snapshot: a flat record of the signal kinds.</summary>
public sealed record DeskSignalState(string Kind, string DeskCode, DateTime TimeUtc, DeskSource Source = default, DeskSessionSignal State = default, int Count = 0,
    bool Degraded = false)
{
    internal static DeskSignalState From(DeskSignal signal) => signal switch
    {
        DeskTransactionStarted s => new("started", s.DeskCode, s.TimeUtc),
        DeskTransactionEnded s => new("ended", s.DeskCode, s.TimeUtc),
        DeskTransactionsCompleted s => new("completed", s.DeskCode, s.TimeUtc, Count: s.Count),
        DeskSessionChangedSignal s => new("session", s.DeskCode, s.TimeUtc, State: s.State),
        DeskZoneReading s => new("zone", s.DeskCode, s.TimeUtc, s.Zone, Count: s.Count, Degraded: s.Degraded),
        DeskHeartbeat s => new("heartbeat", s.DeskCode, s.TimeUtc, s.Of),
        _ => throw new ArgumentException($"Unknown signal {signal?.GetType().Name}.", nameof(signal))
    };

    internal DeskSignal ToSignal()
    {
        var t = DateTime.SpecifyKind(TimeUtc, DateTimeKind.Utc);
        return Kind switch
        {
            "started" => new DeskTransactionStarted(DeskCode, t),
            "ended" => new DeskTransactionEnded(DeskCode, t),
            "completed" => new DeskTransactionsCompleted(DeskCode, t, Count),
            "session" => new DeskSessionChangedSignal(DeskCode, t, State),
            "zone" => new DeskZoneReading(DeskCode, t, Source, Count, Degraded),
            "heartbeat" => new DeskHeartbeat(DeskCode, t, Source),
            _ => throw new InvalidDataException($"Unknown desk signal kind '{Kind}' in a snapshot.")
        };
    }
}

/// <summary>A pending signal with its arrival order and whether it was ahead of the clock.</summary>
public sealed record DeskPendingState(DeskSignalState Signal, bool Ahead, long Sequence);

/// <summary>One desk in a snapshot: what it knows, where it is, and its open minute.</summary>
public sealed record DeskState(
    string DeskCode,
    DeskMemory Memory,
    DateTime CursorUtc,
    DeskEvaluation Current,
    IReadOnlyList<DeskPendingState> Pending,
    IReadOnlyList<long> Ticks,
    long SensorTicks,
    long PresentTicks,
    bool MinuteDegraded,
    int Transactions);

/// <summary>Everything a <see cref="DeskStateEngine"/> holds (ARV-034 snapshots); plain data for System.Text.Json.</summary>
public sealed record DeskEngineState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public DateTime WatermarkUtc { get; init; }
    public long Sequence { get; init; }
    public IReadOnlyList<DeskState> Desks { get; init; } = [];
    public DeskEngineCounters Counters { get; init; }
}

public sealed partial class DeskStateEngine
{
    /// <summary>The engine's whole state.</summary>
    public DeskEngineState Capture() => new()
    {
        WatermarkUtc = _watermark,
        Sequence = _sequence,
        Counters = Counters,
        Desks = [.. _order.Select(d => new DeskState(d.Profile.DeskCode, d.Memory, d.Cursor, d.Current,
            [.. d.Pending.UnorderedItems.OrderBy(i => i.Priority.Ticks).ThenBy(i => i.Priority.Sequence)
                .Select(i => new DeskPendingState(DeskSignalState.From(i.Element.Signal), i.Element.Ahead, i.Priority.Sequence))],
            [.. d.Ticks], d.SensorTicks, d.PresentTicks, d.MinuteDegraded, d.Transactions))]
    };

    /// <summary>
    /// An engine for <paramref name="desks"/> in the captured state. Desks in the snapshot that are no longer configured
    /// are dropped; configured desks missing from it start fresh at the snapshot's watermark (a topology change).
    /// </summary>
    public static DeskStateEngine Restore(IEnumerable<DeskProfile> desks, DeskStateSettings settings, DeskEngineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != DeskEngineState.CurrentVersion)
            throw new InvalidDataException($"Desk snapshot version {state.Version} is not {DeskEngineState.CurrentVersion}.");
        var watermark = DateTime.SpecifyKind(state.WatermarkUtc, DateTimeKind.Utc);
        if (watermark.Year >= 9000)
            throw new InvalidDataException("The desk snapshot's watermark is not plausible.");
        var engine = new DeskStateEngine(desks, watermark, settings);
        var s = engine._settings;
        if ((state.Desks?.Count ?? 0) > s.MaxDesks)
            throw new InvalidDataException("The desk snapshot holds more desks than the engine keeps.");
        var perDesk = Math.Min(s.MaxBufferedSignalsPerDesk, s.MaxBufferedSignals);
        foreach (var saved in state.Desks ?? [])
        {
            if (saved?.DeskCode is null || !engine._desks.TryGetValue(saved.DeskCode, out var desk))
                continue;
            if ((saved.Pending?.Count ?? 0) > perDesk || saved.Ticks is not { Count: 5 } || saved.Memory is null || saved.Current is null ||
                saved.Ticks.Any(t => t is < 0 or > TimeSpan.TicksPerMinute) || saved.Ticks.Sum() > TimeSpan.TicksPerMinute ||
                saved.SensorTicks is < 0 or > TimeSpan.TicksPerMinute || saved.PresentTicks is < 0 or > TimeSpan.TicksPerMinute ||
                saved.Transactions < 0 || !Enum.IsDefined(saved.Current.Status) || saved.CursorUtc > watermark.AddDays(1))
                throw new InvalidDataException($"The snapshot of desk {saved.DeskCode} is not valid.");
            desk.Memory = saved.Memory;
            desk.Cursor = DateTime.SpecifyKind(saved.CursorUtc, DateTimeKind.Utc);
            desk.Current = saved.Current;
            for (var k = 0; k < 5; k++)
                desk.Ticks[k] = saved.Ticks[k];
            (desk.SensorTicks, desk.PresentTicks, desk.MinuteDegraded, desk.Transactions) = (saved.SensorTicks, saved.PresentTicks, saved.MinuteDegraded, saved.Transactions);
            foreach (var p in saved.Pending ?? [])
            {
                if (p?.Signal is null || !string.Equals(p.Signal.DeskCode, saved.DeskCode, StringComparison.Ordinal))
                    throw new InvalidDataException($"The snapshot of desk {saved.DeskCode} has a signal for another desk.");
                if (engine._buffered >= s.MaxBufferedSignals)
                    throw new InvalidDataException("The desk snapshot holds more signals than the engine keeps.");
                var signal = p.Signal.ToSignal();
                // A buffered signal passes the checks Offer applies (ARV-116: a zone reading of a role the desk has, a count within bounds).
                if (signal.TimeUtc.Kind != DateTimeKind.Utc || !engine.Valid(signal, desk.Profile))
                    throw new InvalidDataException($"The snapshot of desk {saved.DeskCode} has a signal the engine would refuse.");
                desk.Pending.Enqueue((signal, p.Ahead), (signal.TimeUtc.Ticks, p.Sequence));
                if (p.Ahead)
                    desk.Ahead++;
                engine._buffered++;
            }
        }

        engine._watermark = watermark;
        engine._sequence = state.Sequence;
        if (state.Counters is { } c)
            (engine._accepted, engine._late, engine._superseded, engine._future, engine._invalid, engine._unknownDesk, engine._bufferFull, engine._tooLate, engine._skippedMinutes) =
                (c.Accepted, c.Late, c.Superseded, c.Future, c.Invalid, c.UnknownDesk, c.BufferFull, c.TooLate, c.SkippedMinutes);
        return engine;
    }
}
