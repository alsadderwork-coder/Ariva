using Ariva.Core.Border;

namespace Ariva.Core.Desks;

/// <summary>The state of one desk (F10). Only <see cref="Idle"/> and <see cref="Serving"/> count as open for throughput.</summary>
public enum DeskStatus
{
    /// <summary>Not staffed: logged out, absent beyond T2, or present without processing.</summary>
    Closed,

    /// <summary>Staffed, not serving.</summary>
    Idle,

    /// <summary>A transaction in progress (or, without a login source, staff and a passenger at the desk).</summary>
    Serving,

    /// <summary>Staffed but without activity for T1, or on break; not open for throughput.</summary>
    Paused,

    /// <summary>Every signal source of the desk is silent; excluded from throughput, the lane's bins are Degraded.</summary>
    Unknown
}

/// <summary>A source of desk signals, in the precedence of F10 (rank 1 to 4).</summary>
public enum DeskSource
{
    /// <summary>Rank 1: transaction start and end (AMAN, common-use check-in).</summary>
    Transactions,

    /// <summary>Rank 2: login and logout (the AMAN desk session, common-use agent sign-in).</summary>
    Session,

    /// <summary>Rank 3: the staff zone behind the desk (sensor).</summary>
    StaffZone,

    /// <summary>Rank 4: the service zone in front of the desk (sensor); weak on its own.</summary>
    ServiceZone,

    /// <summary>
    /// Not a rank (ARV-149): the lane context of a desk without staff or service zones, the expected service time of the
    /// lane it serves and the people waiting in its queue (<see cref="DeskLaneContext"/>). It never makes a desk's state
    /// live or known: only the four sources above do.
    /// </summary>
    LaneContext
}

/// <summary>A desk session state as a login source reports it (AMAN's "on break" is <see cref="Paused"/>).</summary>
public enum DeskSessionSignal
{
    Opened,
    Closed,
    Paused
}

/// <summary>
/// One desk and the signal sources it has. A source that is configured but silent beyond its staleness threshold is
/// treated as absent, so the state falls back to the sources that remain (and is flagged).
/// </summary>
public sealed record DeskProfile(
    string DeskCode,
    string Lane,
    bool HasTransactions,
    bool HasSession,
    bool HasStaffZone,
    bool HasServiceZone)
{
    public bool Has(DeskSource source) => source switch
    {
        DeskSource.Transactions => HasTransactions,
        DeskSource.Session => HasSession,
        DeskSource.StaffZone => HasStaffZone,
        DeskSource.ServiceZone => HasServiceZone,
        _ => false
    };
}

/// <summary>
/// A desk signal in event time (UTC). Signals carry no identities: no officer, traveller or document (the AMAN data
/// boundary); a transaction is only its start or end.
/// </summary>
public abstract record DeskSignal(string DeskCode, DateTime TimeUtc)
{
    /// <summary>The source the signal comes from (and proves alive).</summary>
    public abstract DeskSource Source { get; }
}

/// <summary>A transaction started at the desk (rank 1).</summary>
public sealed record DeskTransactionStarted(string DeskCode, DateTime TimeUtc) : DeskSignal(DeskCode, TimeUtc)
{
    public override DeskSource Source => DeskSource.Transactions;
}

/// <summary>A transaction ended at the desk (rank 1).</summary>
public sealed record DeskTransactionEnded(string DeskCode, DateTime TimeUtc) : DeskSignal(DeskCode, TimeUtc)
{
    public override DeskSource Source => DeskSource.Transactions;
}

/// <summary>
/// Transactions completed over a past interval ending at <see cref="DeskSignal.TimeUtc"/> (AMAN's per-minute desk
/// statistics, where starts and ends are not published): activity, not a transaction in progress.
/// </summary>
public sealed record DeskTransactionsCompleted(string DeskCode, DateTime TimeUtc, int Count) : DeskSignal(DeskCode, TimeUtc)
{
    public override DeskSource Source => DeskSource.Transactions;
}

/// <summary>The desk session changed (rank 2).</summary>
public sealed record DeskSessionChangedSignal(string DeskCode, DateTime TimeUtc, DeskSessionSignal State) : DeskSignal(DeskCode, TimeUtc)
{
    public override DeskSource Source => DeskSource.Session;
}

/// <summary>
/// An occupancy reading of the desk's staff zone (rank 3) or service zone (rank 4). <see cref="Degraded"/>: the sensing
/// pipeline flagged the reading (a corrected or unreliable clock, F11), so the state it supports is Degraded while it is
/// the zone's latest reading (ARV-116).
/// </summary>
public sealed record DeskZoneReading(string DeskCode, DateTime TimeUtc, DeskSource Zone, int Count, bool Degraded = false) : DeskSignal(DeskCode, TimeUtc)
{
    public override DeskSource Source => Zone;
}

/// <summary>A source is alive without news (a feed or device heartbeat): it keeps the source from going stale.</summary>
public sealed record DeskHeartbeat(string DeskCode, DateTime TimeUtc, DeskSource Of) : DeskSignal(DeskCode, TimeUtc)
{
    public override DeskSource Source => Of;
}

/// <summary>
/// The lane context of a desk without staff or service zones at <see cref="DeskSignal.TimeUtc"/> (F10 rows 5a to 5f,
/// ARV-149): <see cref="ServiceSeconds"/>, the expected service time per person of the lane it serves (the lane cycle time
/// of ARV-117d, <see cref="LaneCycle.PerPerson"/>), and <see cref="Waiting"/>, the people in the queue zones it serves;
/// each null when not known. <see cref="Degraded"/>: a queue length it rests on was degraded (F11). Aggregates only: no
/// officer, traveller or document identity. It keeps no source alive.
/// </summary>
public sealed record DeskLaneContext(string DeskCode, DateTime TimeUtc, double? ServiceSeconds, int? Waiting, bool Degraded = false) : DeskSignal(DeskCode, TimeUtc)
{
    /// <summary>The longest expected service time taken, in seconds (contract V1's bound on a service time).</summary>
    public const double MaxServiceSeconds = ImmigrationRules.MaxSeconds;

    /// <summary>The most people a context may count as waiting (CWE-501, Proposed); more is not a queue.</summary>
    public const int MaxWaiting = 100_000;

    public override DeskSource Source => DeskSource.LaneContext;

    /// <summary>Whether the values are within bounds (CWE-501): a service time finite, above 0 and at most an hour; 0 to <see cref="MaxWaiting"/> waiting.</summary>
    public bool WithinBounds => (ServiceSeconds is null || ServiceWithinBounds(ServiceSeconds.Value)) && (Waiting is null || WaitingWithinBounds(Waiting.Value));

    /// <summary>An expected service time in seconds that can be taken: finite, above 0 and at most <see cref="MaxServiceSeconds"/>.</summary>
    public static bool ServiceWithinBounds(double seconds) => double.IsFinite(seconds) && seconds > 0 && seconds <= MaxServiceSeconds;

    /// <summary>A count of people waiting that can be taken: 0 to <see cref="MaxWaiting"/>.</summary>
    public static bool WaitingWithinBounds(int waiting) => waiting is >= 0 and <= MaxWaiting;
}

/// <summary>Settings of the desk state engine (F10; Proposed values to tune in the pilot).</summary>
public sealed record DeskStateSettings
{
    /// <summary>T1: staffed without activity this long is Paused (example 3 minutes).</summary>
    public TimeSpan PauseAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// T1 for a desk without a live login source (ARV-116, Proposed): staff presence is then the only evidence that the
    /// desk is staffed, so a staff zone empty this long (with no transaction in that time) makes it Paused, while a sensor
    /// dropout shorter than this keeps it open. Null: <see cref="PauseAfter"/>, as before. The desk feed sets 60 seconds.
    /// </summary>
    public TimeSpan? SensorPauseAfter { get; init; }

    /// <summary>The T1 that applies: <see cref="PauseAfter"/> with a live login source, <see cref="SensorPauseAfter"/> (if set) without.</summary>
    public TimeSpan PauseAfterFor(bool loginLive) => loginLive ? PauseAfter : SensorPauseAfter ?? PauseAfter;

    /// <summary>T2: staffed with the staff zone empty this long is Closed (example 10 minutes).</summary>
    public TimeSpan CloseAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>T_stale per source: silent this long, a source counts as absent (Proposed 2 x a 60-second heartbeat).</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A transaction open longer than this is a lost end, not a passenger being served.</summary>
    public TimeSpan MaxTransaction { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long signals may arrive after their time and still be applied in order.</summary>
    public TimeSpan Lateness { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Signals further ahead of the reference clock than this are refused.</summary>
    public TimeSpan MaxAhead { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Desks one engine holds at most.</summary>
    public int MaxDesks { get; init; } = 5_000;

    /// <summary>Signals held at most for reordering.</summary>
    public int MaxBufferedSignals { get; init; } = 100_000;

    /// <summary>Signals held at most for one desk, so that a flood on one desk cannot crowd out the others.</summary>
    public int MaxBufferedSignalsPerDesk { get; init; } = 1_000;

    /// <summary>Signals older than this behind the watermark are refused (a replay belongs in an engine started at its own time).</summary>
    public TimeSpan MaxLate { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>A desk further behind the watermark than this skips the gap (counted) rather than emitting every minute of it.</summary>
    public TimeSpan MaxCatchUp { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Minutes and transitions one step holds at most; beyond it the step ends early (<see cref="DeskStep.More"/>).</summary>
    public int MaxStepRecords { get; init; } = 100_000;

    /// <summary>People one zone reading may count; more is not a desk.</summary>
    public int MaxZoneCount { get; init; } = 50;

    /// <summary>Transactions one completed-interval signal may carry.</summary>
    public int MaxTransactionsPerSignal { get; init; } = 1_000;

    /// <summary>
    /// k of F10 rows 5b to 5e (ARV-149, Proposed 4, from 2 to 20): a desk without staff or service zones is Paused only after
    /// a silence of k times its lane's expected service time s (never less than T1, never more than <see cref="MaxTransaction"/>).
    /// </summary>
    public double RhythmPauseMultiple { get; init; } = 4;

    /// <summary>
    /// I of F10 row 5a (ARV-149): the interval one <see cref="DeskTransactionsCompleted"/> covers (AMAN contract V1: one
    /// minute). A transaction is counted in the interval it ends in and reported at the interval's end, so at a desk serving
    /// one traveller after another the next interval with a transaction ends at most s + I after the last.
    /// </summary>
    public TimeSpan CompletedInterval { get; init; } = TimeSpan.FromSeconds(ImmigrationRules.IntervalSeconds);

    /// <summary>
    /// P of F10 rows 5b to 5e (ARV-149): max(T1, min(k x s, <see cref="MaxTransaction"/>)), or T1 when the lane's expected
    /// service time <paramref name="service"/> is not known.
    /// </summary>
    public TimeSpan RhythmPauseAfter(TimeSpan? service)
    {
        if (!service.HasValue)
            return PauseAfter;
        var seconds = service.Value.TotalSeconds * RhythmPauseMultiple;
        var multiple = double.IsFinite(seconds) && seconds < MaxTransaction.TotalSeconds ? TimeSpan.FromSeconds(seconds) : MaxTransaction;
        return multiple > PauseAfter ? multiple : PauseAfter;
    }

    /// <summary>The problems with these settings, empty when valid.</summary>
    public IEnumerable<string> Problems()
    {
        if (PauseAfter <= TimeSpan.Zero || PauseAfter > TimeSpan.FromHours(2))
            yield return "PauseAfter (T1) is above 0 and at most 2 hours.";
        if (CloseAfter <= PauseAfter || CloseAfter > TimeSpan.FromHours(4))
            yield return "CloseAfter (T2) is above PauseAfter (T1) and at most 4 hours.";
        // No pattern variable in the condition (ARV-069a).
        var sensorPause = SensorPauseAfter;
        if (sensorPause.HasValue && (sensorPause.Value < TimeSpan.FromSeconds(10) || sensorPause.Value > PauseAfter))
            yield return "SensorPauseAfter is from 10 seconds to PauseAfter (T1).";
        if (StaleAfter < TimeSpan.FromSeconds(10) || StaleAfter > TimeSpan.FromHours(1))
            yield return "StaleAfter is from 10 seconds to 1 hour.";
        if (MaxTransaction < TimeSpan.FromMinutes(1) || MaxTransaction > TimeSpan.FromHours(4))
            yield return "MaxTransaction is from 1 minute to 4 hours.";
        if (Lateness < TimeSpan.Zero || Lateness > TimeSpan.FromMinutes(30))
            yield return "Lateness is from 0 to 30 minutes.";
        if (MaxAhead < TimeSpan.Zero || MaxAhead > TimeSpan.FromMinutes(30))
            yield return "MaxAhead is from 0 to 30 minutes.";
        if (MaxDesks is < 1 or > 100_000)
            yield return "MaxDesks is from 1 to 100,000.";
        if (MaxBufferedSignals is < 100 or > 10_000_000)
            yield return "MaxBufferedSignals is from 100 to 10,000,000.";
        if (MaxBufferedSignalsPerDesk is < 10 or > 10_000_000)
            yield return "MaxBufferedSignalsPerDesk is from 10 to 10,000,000 (and never more than MaxBufferedSignals).";
        if (MaxLate < Lateness || MaxLate > TimeSpan.FromDays(1))
            yield return "MaxLate is from Lateness to 1 day.";
        if (MaxCatchUp < TimeSpan.FromHours(1) || MaxCatchUp > TimeSpan.FromDays(7))
            yield return "MaxCatchUp is from 1 hour to 7 days.";
        if ((long)MaxDesks * Math.Min(MaxBufferedSignalsPerDesk, MaxBufferedSignals) > 20_000_000)
            yield return "MaxDesks x MaxBufferedSignalsPerDesk is at most 20,000,000 (the queue memory a desk may keep).";
        if (MaxStepRecords is < 100 or > 10_000_000)
            yield return "MaxStepRecords is from 100 to 10,000,000.";
        if (MaxZoneCount is < 1 or > 10_000)
            yield return "MaxZoneCount is from 1 to 10,000.";
        if (MaxTransactionsPerSignal is < 1 or > 1_000_000)
            yield return "MaxTransactionsPerSignal is from 1 to 1,000,000.";
        if (!double.IsFinite(RhythmPauseMultiple) || RhythmPauseMultiple < 2 || RhythmPauseMultiple > 20)
            yield return "RhythmPauseMultiple is from 2 to 20.";
        if (CompletedInterval < TimeSpan.FromSeconds(10) || CompletedInterval > TimeSpan.FromMinutes(15))
            yield return "CompletedInterval is from 10 seconds to 15 minutes.";
    }
}

/// <summary>What the engine knows of one desk's signals at a moment (no identities).</summary>
public sealed record DeskMemory
{
    /// <summary>When each source was last heard (a signal or heartbeat).</summary>
    public DateTime? TransactionsHeard { get; init; }

    public DateTime? SessionHeard { get; init; }

    public DateTime? StaffHeard { get; init; }

    public DateTime? ServiceHeard { get; init; }

    /// <summary>When <paramref name="source"/> was last heard.</summary>
    public DateTime? Heard(DeskSource source) => source switch
    {
        DeskSource.Transactions => TransactionsHeard,
        DeskSource.Session => SessionHeard,
        DeskSource.StaffZone => StaffHeard,
        DeskSource.ServiceZone => ServiceHeard,
        _ => null
    };

    /// <summary>This memory with <paramref name="source"/> heard at <paramref name="atUtc"/> (never moving it back).</summary>
    public DeskMemory WithHeard(DeskSource source, DateTime atUtc)
    {
        var heard = Heard(source);
        if (heard.HasValue && heard.Value >= atUtc)
            return this;
        return source switch
        {
            DeskSource.Transactions => this with { TransactionsHeard = atUtc },
            DeskSource.Session => this with { SessionHeard = atUtc },
            DeskSource.StaffZone => this with { StaffHeard = atUtc },
            DeskSource.ServiceZone => this with { ServiceHeard = atUtc },
            _ => this
        };
    }

    /// <summary>The latest session state and when it was reported (null: never logged in as far as known).</summary>
    public DeskSessionSignal? Session { get; init; }

    public DateTime? SessionSince { get; init; }

    /// <summary>The time of the latest session signal applied, repeats included (to recognise superseded late ones).</summary>
    public DateTime? SessionSignalAt { get; init; }

    /// <summary>The start of the transaction in progress (null: none).</summary>
    public DateTime? TransactionSince { get; init; }

    /// <summary>The latest transaction activity (a start, an end or a completed interval).</summary>
    public DateTime? LastTransaction { get; init; }

    /// <summary>The time of the latest transaction signal applied (to recognise superseded late ones).</summary>
    public DateTime? TransactionSignalAt { get; init; }

    /// <summary>The latest staff and service zone counts and when they were read.</summary>
    public int? StaffCount { get; init; }

    public DateTime? StaffReadAt { get; init; }

    /// <summary>Since when the staff zone has been empty as far as known (null: occupied or unknown).</summary>
    public DateTime? StaffEmptySince { get; init; }

    /// <summary>The staff zone emptied from an occupied reading (someone left), rather than being empty since it was first or again heard.</summary>
    public bool StaffLeft { get; init; }

    public int? ServiceCount { get; init; }

    public DateTime? ServiceReadAt { get; init; }

    /// <summary>The latest staff and service zone readings were flagged by the sensing pipeline (F11, ARV-116); absent in older snapshots, so false.</summary>
    public bool StaffDegraded { get; init; }

    public bool ServiceDegraded { get; init; }

    /// <summary>
    /// ARV-149 (a desk without staff or service zones, F10 rows 5a to 5f): the time of the latest lane context taken; null
    /// before any (absent in older snapshots).
    /// </summary>
    public DateTime? LaneContextAt { get; init; }

    /// <summary>ARV-149: the lane's expected service time per person in seconds from that context (null: not known).</summary>
    public double? LaneServiceSeconds { get; init; }

    /// <summary>ARV-149: the people waiting in the desk's queue zones from that context (null: not known).</summary>
    public int? QueueWaiting { get; init; }

    /// <summary>ARV-149: since when people have been waiting without a break in the contexts (null: nobody waiting, or not known).</summary>
    public DateTime? QueueWaitingSince { get; init; }

    /// <summary>ARV-149: a queue length that context rests on was degraded (F11).</summary>
    public bool QueueDegraded { get; init; }

    public static DeskMemory Empty { get; } = new();
}

/// <summary>The evaluated state of a desk: its status and how it was derived.</summary>
public sealed record DeskEvaluation(DeskStatus Status, bool SensorDerived, bool PresentNotProcessing, bool Degraded)
{
    /// <summary>Open for throughput (F8 n_open): Idle or Serving.</summary>
    public bool Open => Status is DeskStatus.Idle or DeskStatus.Serving;
}

/// <summary>
/// The desk state rule of formula F10, evaluated at a moment from what is known of each source. First matching row
/// wins: (1) every source stale gives Unknown; (2) a live login source whose latest session is a logout gives Closed;
/// (3) a transaction in progress gives Serving; (3a, Proposed) a live login source on break gives Paused; (4) staffed
/// with the staff zone empty for T2 and no transaction in that time gives Closed; (5) staffed with no activity for T1
/// (the staff zone empty and no transaction, or without a staff zone sensor no transaction since T1 or since login;
/// without a live login source T1 is <see cref="DeskStateSettings.SensorPauseAfter"/> when set, ARV-116)
/// gives Paused, where staffed without a live login source means staff present or within T2 of the staff leaving or of
/// the last transaction (Proposed); (6) without a live login source, staff present and the service zone occupied gives Serving, flagged
/// as sensor-derived; (7) staffed gives Idle; (8) a live login source not logged in with staff present gives Closed,
/// recorded as present and not processing; (9) Closed. A configured source that is stale counts as absent, and the
/// result is flagged Degraded; a staff or service zone counts as configured only once it has been heard (ARV-116).
/// A desk whose only sources are a login and transactions (no staff or service zone, <see cref="FollowsRhythm"/>), logged in
/// with both live, takes rows 5a to 5f instead of rows 4 to 7 (ARV-149, Proposed): (5a) transactions keep arriving within its
/// lane's expected service time s (the last interval with a transaction ended less than min(s + I, P) ago) gives Serving;
/// with people waiting in its queue, (5b) Serving while the silence since its last activity or the start of the waiting is
/// under P = max(T1, min(k x s, the longest transaction)), (5c) Paused after it; (5d) nobody waiting gives Idle; with the
/// queue not known, (5e) Paused after a silence of P, (5f) otherwise Idle. Without a lane context (or a stale one) these are
/// rows 5 and 7 as before. Pure.
/// </summary>
public static class DeskRule
{
    public static bool Live(DeskProfile desk, DeskMemory memory, DeskSource source, DateTime atUtc, DeskStateSettings settings)
    {
        // No pattern variable in the condition (ARV-069a): a mutant that short-circuits it still compiles.
        var heard = memory.Heard(source);
        return desk.Has(source) && heard.HasValue && heard.Value <= atUtc && atUtc - heard.Value < settings.StaleAfter;
    }

    /// <summary>
    /// Whether F10 rows 5a to 5f apply to <paramref name="desk"/> (ARV-149): a login and a transaction source and neither a
    /// staff nor a service zone, so its state comes from AMAN alone. Desks with zones keep rows 4 to 7.
    /// </summary>
    public static bool FollowsRhythm(DeskProfile desk) =>
        desk is not null && desk.HasSession && desk.HasTransactions && !desk.HasStaffZone && !desk.HasServiceZone;

    /// <summary>
    /// The lane context a desk is judged against at <paramref name="atUtc"/> (ARV-149): the lane's expected service time s,
    /// the people waiting and whether a degraded queue length decided it; nothing known when the latest context is older
    /// than T_stale (or newer than the moment), and a value out of bounds counts as not known.
    /// </summary>
    public static (TimeSpan? Service, int? Waiting, bool Degraded) Context(DeskMemory memory, DateTime atUtc, DeskStateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(settings);
        var at = memory.LaneContextAt;
        if (!at.HasValue || at.Value > atUtc || atUtc - at.Value >= settings.StaleAfter)
            return (null, null, false);
        var waiting = memory.QueueWaiting;
        var known = waiting.HasValue && DeskLaneContext.WaitingWithinBounds(waiting.Value);
        return (ServiceOf(memory.LaneServiceSeconds), known ? waiting : null, known && memory.QueueDegraded);
    }

    public static DeskEvaluation Evaluate(DeskProfile desk, DeskMemory memory, DateTime atUtc, DeskStateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(settings);
        var tx = Live(desk, memory, DeskSource.Transactions, atUtc, settings);
        var login = Live(desk, memory, DeskSource.Session, atUtc, settings);
        var staff = Live(desk, memory, DeskSource.StaffZone, atUtc, settings);
        var service = Live(desk, memory, DeskSource.ServiceZone, atUtc, settings);
        // A staff or service zone counts as a source once it has been heard (ARV-116, Proposed): a profile may draw zones
        // that no sensor covers (to link a queue to its desks), and such a zone must not degrade the desk for good; once
        // heard, a zone that falls silent is stale like any other source.
        bool Installed(DeskSource zone)
        {
            var heard = memory.Heard(zone);
            return heard.HasValue && heard.Value <= atUtc;
        }
        var configured = (desk.HasTransactions ? 1 : 0) + (desk.HasSession ? 1 : 0) + (desk.HasStaffZone && Installed(DeskSource.StaffZone) ? 1 : 0) +
                         (desk.HasServiceZone && Installed(DeskSource.ServiceZone) ? 1 : 0);
        var live = (tx ? 1 : 0) + (login ? 1 : 0) + (staff ? 1 : 0) + (service ? 1 : 0);

        // Row 1: nothing to go on.
        if (live == 0)
            return new DeskEvaluation(DeskStatus.Unknown, false, false, true);
        // A configured source gone stale, or a live zone reading the sensing pipeline flagged (F11), degrades the state.
        var degraded = live < configured || (staff && memory.StaffDegraded) || (service && memory.ServiceDegraded);

        var staffPresent = staff && memory.StaffCount > 0;

        // Row 2: logout proves Closed whatever the sensors show (staff still present are recorded, as in row 8).
        if (login && memory.Session == DeskSessionSignal.Closed)
            return new DeskEvaluation(DeskStatus.Closed, false, staffPresent, degraded);

        // Row 3: a transaction in progress proves Serving (a start without an end, not older than a lost end).
        var since = memory.TransactionSince;
        if (tx && since.HasValue && since.Value <= atUtc && atUtc - since.Value < settings.MaxTransaction)
            return new DeskEvaluation(DeskStatus.Serving, false, false, degraded);

        // Row 3a (Proposed): the login source's own "on break".
        if (login && memory.Session == DeskSessionSignal.Paused)
            return new DeskEvaluation(DeskStatus.Paused, false, false, degraded);

        var empty = memory.StaffEmptySince;
        var emptyFor = staff && memory.StaffCount == 0 && empty.HasValue && empty.Value <= atUtc ? atUtc - empty.Value : (TimeSpan?)null;
        var recentTransaction = tx && Within(memory.LastTransaction, atUtc, settings.CloseAfter);

        // Staffed: logged in; without a live login source, staff present, or (Proposed) within T2 of the staff leaving
        // or of the last transaction, so that rows 4 and 5 give their grace and a sensor dropout does not close the desk.
        var staffedByZone = staffPresent || (memory.StaffLeft && emptyFor < settings.CloseAfter);
        var staffed = login ? memory.Session == DeskSessionSignal.Opened : staffedByZone || recentTransaction;
        if (staffed)
        {
            // Activity is a transaction (rank 1) and, where a staff zone sensor exists, staff presence; the start of
            // the session counts as activity so that a fresh login is not Paused at once.
            var lastActivity = Max(tx ? memory.LastTransaction : null, login ? memory.SessionSince : null);

            // Rows 5a to 5f (ARV-149, Proposed): a desk with AMAN's sources alone follows its transaction rhythm against the
            // lane's expected service time and its queue; with no lane context they are rows 5 and 7 as before.
            if (login && tx && FollowsRhythm(desk))
                return Rhythm(memory, atUtc, settings, lastActivity, degraded);

            // Row 4: staff zone empty for T2, with no transaction in that time (Proposed: rank 1 outranks a missed zone).
            if (emptyFor >= settings.CloseAfter && !Within(lastActivity, atUtc, settings.CloseAfter))
                return new DeskEvaluation(DeskStatus.Closed, false, false, degraded);

            // Row 5: no activity for T1 (without a live login source, the shorter sensor T1 when set: ARV-116, Proposed).
            var pauseAfter = settings.PauseAfterFor(login);
            if (staff
                    ? emptyFor >= pauseAfter && !Within(lastActivity, atUtc, pauseAfter)
                    : tx && !Within(lastActivity, atUtc, pauseAfter))
                return new DeskEvaluation(DeskStatus.Paused, false, false, degraded);
        }

        // Row 6: without a login source, staff present and a passenger at the desk.
        if (!login && staffPresent && service && memory.ServiceCount > 0)
            return new DeskEvaluation(DeskStatus.Serving, true, false, degraded);

        // Row 7.
        if (staffed)
            return new DeskEvaluation(DeskStatus.Idle, !login && !recentTransaction, false, degraded);

        // Row 8: present, not processing (a supervisor or training).
        if (login && staffPresent)
            return new DeskEvaluation(DeskStatus.Closed, false, true, degraded);

        // Row 9.
        return new DeskEvaluation(DeskStatus.Closed, false, false, degraded);
    }

    // F10 rows 5a to 5f (ARV-149): a logged-in desk with live session and transaction sources and no staff or service zone.
    // AMAN's interval statistics report completed transactions only, at the end of the interval they ended in, so the
    // rhythm is judged from the end of the last interval with a transaction. Not sensor-derived: the login is AMAN's.
    private static DeskEvaluation Rhythm(DeskMemory memory, DateTime atUtc, DeskStateSettings settings, DateTime? lastActivity, bool degraded)
    {
        var context = Context(memory, atUtc, settings);
        var pause = settings.RhythmPauseAfter(context.Service);

        // Row 5a: transactions keep arriving within the lane's expected service time.
        if (context.Service.HasValue)
        {
            var rhythm = context.Service.Value + settings.CompletedInterval;
            if (Within(memory.LastTransaction, atUtc, rhythm < pause ? rhythm : pause))
                return new DeskEvaluation(DeskStatus.Serving, false, false, degraded);
        }

        if (context.Waiting.HasValue)
        {
            var queueDegraded = degraded || context.Degraded;
            // Row 5d: logged in with nobody waiting.
            if (context.Waiting.Value == 0)
                return new DeskEvaluation(DeskStatus.Idle, false, false, queueDegraded);

            // Rows 5b and 5c: people waiting; the silence counts from the last activity or the start of the waiting, so a
            // desk idle through a quiet spell has P to start once a queue forms.
            var since = Max(lastActivity, memory.QueueWaitingSince);
            return new DeskEvaluation(Within(since, atUtc, pause) ? DeskStatus.Serving : DeskStatus.Paused, false, false, queueDegraded);
        }

        // Rows 5e and 5f: the queue not known; P after the last activity (T1 without s, as row 5 before).
        return new DeskEvaluation(Within(lastActivity, atUtc, pause) ? DeskStatus.Idle : DeskStatus.Paused, false, false, degraded);
    }

    // An expected service time from a lane context, or null when absent or out of bounds (a restored snapshot is data, CWE-501).
    private static TimeSpan? ServiceOf(double? seconds) =>
        seconds.HasValue && DeskLaneContext.ServiceWithinBounds(seconds.Value) ? TimeSpan.FromSeconds(seconds.Value) : null;

    /// <summary>
    /// The earliest moment after <paramref name="afterUtc"/> at which the evaluation could change without a new signal
    /// (a source going stale, T1 or T2 passing, a transaction becoming a lost end), or null when none.
    /// </summary>
    public static DateTime? NextChange(DeskProfile desk, DeskMemory memory, DateTime afterUtc, DeskStateSettings settings)
    {
        DateTime? next = null;
        // No pattern variables in the conditions (ARV-069a): every mutant of them compiles, so Stryker measures this method.
        void Consider(DateTime? at, TimeSpan plus)
        {
            if (at is null)
                return;
            var t = at.Value;
            var candidate = t > DateTime.MaxValue - plus ? DateTime.MaxValue : t + plus;
            if (candidate > afterUtc && (next is null || candidate < next))
                next = candidate;
        }

        foreach (var source in (DeskSource[])[DeskSource.Transactions, DeskSource.Session, DeskSource.StaffZone, DeskSource.ServiceZone])
            if (desk.Has(source))
                Consider(memory.Heard(source), settings.StaleAfter);
        Consider(memory.StaffEmptySince, settings.PauseAfter);
        Consider(memory.StaffEmptySince, settings.CloseAfter);
        Consider(memory.LastTransaction, settings.PauseAfter);
        var sensorPause = settings.SensorPauseAfter;
        if (sensorPause.HasValue)
        {
            Consider(memory.StaffEmptySince, sensorPause.Value);
            Consider(memory.LastTransaction, sensorPause.Value);
        }

        Consider(memory.LastTransaction, settings.CloseAfter);
        Consider(memory.SessionSince, settings.PauseAfter);
        Consider(memory.SessionSince, settings.CloseAfter);
        Consider(memory.TransactionSince, settings.MaxTransaction);

        // ARV-149, rows 5a to 5f: the lane context going stale, the rhythm window and P after each kind of activity, with the
        // context's expected service time and without it (T1, considered above for the transaction and the login).
        if (FollowsRhythm(desk))
        {
            Consider(memory.LaneContextAt, settings.StaleAfter);
            Consider(memory.QueueWaitingSince, settings.PauseAfter);
            var service = ServiceOf(memory.LaneServiceSeconds);
            if (service.HasValue)
            {
                var pause = settings.RhythmPauseAfter(service);
                Consider(memory.LastTransaction, service.Value + settings.CompletedInterval);
                Consider(memory.LastTransaction, pause);
                Consider(memory.SessionSince, pause);
                Consider(memory.QueueWaitingSince, pause);
            }
        }

        return next;
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    // Activity within the last span before atUtc (activity in the future of atUtc counts as now).
    private static bool Within(DateTime? last, DateTime atUtc, TimeSpan span) =>
        last.HasValue && (last.Value >= atUtc || atUtc - last.Value < span);
}
