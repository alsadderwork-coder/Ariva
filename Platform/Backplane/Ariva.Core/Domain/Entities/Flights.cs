using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.Events;
using Ariva.Core.Flights;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// One flight leg at a site (ARV-041, Flight Demand context), keyed by the feed's stable flight key. Feeds arrive out of
/// order, so nothing here depends on the order messages come in: the schedule (identity, airports, places, aircraft,
/// counts, codeshares) is replaced only by a message at least as recent as the one that set it, and each milestone
/// (estimate, actual, on-block, gate open, boarding, off-block, cancelled, diverted) keeps the value of the most recent
/// message that reported it; an older message can still fill a milestone nobody reported yet. The status follows from
/// the milestones known. A change raises <see cref="FlightChanged"/>. The direction never changes.
/// </summary>
public class FlightLeg : EntityBase<FlightLeg>, ISiteBound
{
    protected FlightLeg()
    {
    }

    public FlightLeg(string siteCode, FlightLegValues values, string feed, DateTime sourceUtc, DateTime receivedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteCode);
        ArgumentNullException.ThrowIfNull(values);
        Id = NewId();
        SiteCode = siteCode;
        FlightKey = values.FlightKey;
        Direction = values.Direction;
        Apply(values, feed, sourceUtc, receivedUtc);
    }

    public virtual string SiteCode { get; protected set; }
    public virtual string FlightKey { get; protected set; }
    public virtual FlightDirection Direction { get; protected set; }
    public virtual string Carrier { get; protected set; }
    public virtual string Number { get; protected set; }
    public virtual string Suffix { get; protected set; }
    public virtual DateTime ScheduledUtc { get; protected set; }
    public virtual string Origin { get; protected set; }
    public virtual string Destination { get; protected set; }
    public virtual string Terminal { get; protected set; }
    public virtual string Stand { get; protected set; }
    public virtual string Gate { get; protected set; }
    public virtual string AircraftType { get; protected set; }
    public virtual int? Seats { get; protected set; }
    public virtual int? PaxEstimate { get; protected set; }

    /// <summary>Codeshare designators, space separated (each checked by <see cref="FlightRules"/>).</summary>
    public virtual string CodeshareCodes { get; protected set; }

    /// <summary>The message time of the schedule fields above.</summary>
    public virtual DateTime ScheduleSourceUtc { get; protected set; }

    /// <summary>Estimated in-block (arrival) or off-block (departure).</summary>
    public virtual DateTime? EstimatedUtc { get; protected set; }

    public virtual DateTime? EstimatedSourceUtc { get; protected set; }

    /// <summary>Landed (arrival) or airborne (departure).</summary>
    public virtual DateTime? ActualUtc { get; protected set; }

    public virtual DateTime? ActualSourceUtc { get; protected set; }
    public virtual DateTime? OnBlockUtc { get; protected set; }
    public virtual DateTime? OnBlockSourceUtc { get; protected set; }
    public virtual DateTime? GateOpenUtc { get; protected set; }
    public virtual DateTime? GateOpenSourceUtc { get; protected set; }
    public virtual DateTime? BoardingStartUtc { get; protected set; }
    public virtual DateTime? BoardingStartSourceUtc { get; protected set; }
    public virtual DateTime? OffBlockUtc { get; protected set; }
    public virtual DateTime? OffBlockSourceUtc { get; protected set; }
    public virtual bool Cancelled { get; protected set; }
    public virtual DateTime? CancelledSourceUtc { get; protected set; }
    public virtual bool Diverted { get; protected set; }
    public virtual DateTime? DivertedSourceUtc { get; protected set; }

    public virtual FlightStatus Status { get; protected set; }

    /// <summary>The feed of the last message that changed this leg, and when Ariva received it.</summary>
    public virtual string Feed { get; protected set; }

    public virtual DateTime UpdatedUtc { get; protected set; }

    public virtual IReadOnlyList<string> Codeshares => string.IsNullOrEmpty(CodeshareCodes) ? [] : CodeshareCodes.Split(' ');

    /// <summary>The estimated time if there is one, else the scheduled time.</summary>
    public virtual DateTime ExpectedUtc => EstimatedUtc ?? ScheduledUtc;

    /// <summary>Takes a snapshot of the leg from a message of <paramref name="sourceUtc"/>; true when anything changed.</summary>
    public virtual bool Apply(FlightLegValues values, string feed, DateTime sourceUtc, DateTime receivedUtc)
    {
        ArgumentNullException.ThrowIfNull(values);
        RequireUtc(sourceUtc, nameof(sourceUtc));
        if (!string.Equals(values.FlightKey, FlightKey, StringComparison.Ordinal))
            throw new ArgumentException("The values are of another flight.", nameof(values));
        if (values.Direction != Direction)
            throw new InvalidOperationException("A flight leg keeps its direction; a different direction is a different flight key.");

        var before = Fingerprint();
        if (Carrier is null || sourceUtc >= ScheduleSourceUtc)
        {
            (Carrier, Number, Suffix, ScheduledUtc) = (values.Carrier, values.Number, values.Suffix, values.ScheduledUtc);
            (Origin, Destination, Terminal, Stand, Gate) = (values.Origin, values.Destination, values.Terminal, values.Stand, values.Gate);
            (AircraftType, Seats, PaxEstimate) = (values.AircraftType, values.Seats, values.PaxEstimate);
            CodeshareCodes = values.Codeshares.Count == 0 ? null : string.Join(' ', values.Codeshares);
            ScheduleSourceUtc = sourceUtc;
        }

        if (values.EstimatedUtc is { } estimated)
            Observe(FlightEventType.Estimated, estimated, sourceUtc);
        if (values.ActualUtc is { } actual)
            ObserveActual(actual, sourceUtc);
        if (values.OnBlockUtc is { } onBlock)
            Observe(FlightEventType.OnBlock, onBlock, sourceUtc);
        if (values.OffBlockUtc is { } offBlock)
            Observe(FlightEventType.OffBlock, offBlock, sourceUtc);
        // A status other than Cancelled or Diverted in a newer message reinstates the flight.
        if (values.Status is { } status)
        {
            Flag(FlightEventType.Cancelled, status == FlightStatus.Cancelled, sourceUtc);
            Flag(FlightEventType.Diverted, status == FlightStatus.Diverted, sourceUtc);
        }

        return Changed(before, feed, receivedUtc);
    }

    /// <summary>Takes one milestone from a message of <paramref name="sourceUtc"/>; true when it changed the leg.</summary>
    public virtual bool Record(FlightEventType type, DateTime timeUtc, string feed, DateTime sourceUtc, DateTime receivedUtc)
    {
        RequireUtc(sourceUtc, nameof(sourceUtc));
        if (!Fits(type))
            throw new InvalidOperationException($"{type} is not a milestone of {(Direction == FlightDirection.Arrival ? "an arrival" : "a departure")}.");
        if (timeUtc.Kind != DateTimeKind.Utc || timeUtc < ScheduledUtc - FlightRules.EarlyBy || timeUtc > ScheduledUtc + FlightRules.LateBy)
            throw new ArgumentOutOfRangeException(nameof(timeUtc), "A milestone is in UTC, at most a day before and 3 days after the schedule.");

        var before = Fingerprint();
        switch (type)
        {
            case FlightEventType.Cancelled or FlightEventType.Diverted:
                Flag(type, true, sourceUtc);
                break;
            case FlightEventType.Landed:
                ObserveActual(timeUtc, sourceUtc);
                break;
            default:
                Observe(type, timeUtc, sourceUtc);
                break;
        }

        return Changed(before, feed, receivedUtc);
    }

    /// <summary>Whether a milestone belongs to this leg's direction.</summary>
    public virtual bool Fits(FlightEventType type) => type switch
    {
        FlightEventType.Landed or FlightEventType.OnBlock => Direction == FlightDirection.Arrival,
        FlightEventType.GateOpen or FlightEventType.BoardingStart or FlightEventType.OffBlock => Direction == FlightDirection.Departure,
        _ => Enum.IsDefined(type)
    };

    /// <summary>The status the known milestones give.</summary>
    public static FlightStatus StatusOf(FlightLeg leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (leg.Cancelled)
            return FlightStatus.Cancelled;
        if (leg.Diverted)
            return FlightStatus.Diverted;
        if (leg.Direction == FlightDirection.Arrival)
        {
            return leg.OnBlockUtc is not null ? FlightStatus.OnBlock
                : leg.ActualUtc is not null ? FlightStatus.Landed
                : leg.EstimatedUtc is not null ? FlightStatus.Estimated
                : FlightStatus.Scheduled;
        }

        return leg.ActualUtc is not null || leg.OffBlockUtc is not null ? FlightStatus.OffBlock
            : leg.BoardingStartUtc is not null ? FlightStatus.Boarding
            : leg.GateOpenUtc is not null ? FlightStatus.GateOpen
            : leg.EstimatedUtc is not null ? FlightStatus.Estimated
            : FlightStatus.Scheduled;
    }

    private void ObserveActual(DateTime actual, DateTime sourceUtc)
    {
        if (ActualSourceUtc is null || sourceUtc >= ActualSourceUtc)
            (ActualUtc, ActualSourceUtc) = (actual, sourceUtc);
    }

    private void Observe(FlightEventType type, DateTime time, DateTime sourceUtc)
    {
        static bool Newer(DateTime? stored, DateTime incoming) => stored is null || incoming >= stored;
        switch (type)
        {
            case FlightEventType.Estimated when Newer(EstimatedSourceUtc, sourceUtc):
                (EstimatedUtc, EstimatedSourceUtc) = (time, sourceUtc);
                break;
            case FlightEventType.OnBlock when Newer(OnBlockSourceUtc, sourceUtc):
                (OnBlockUtc, OnBlockSourceUtc) = (time, sourceUtc);
                break;
            case FlightEventType.GateOpen when Newer(GateOpenSourceUtc, sourceUtc):
                (GateOpenUtc, GateOpenSourceUtc) = (time, sourceUtc);
                break;
            case FlightEventType.BoardingStart when Newer(BoardingStartSourceUtc, sourceUtc):
                (BoardingStartUtc, BoardingStartSourceUtc) = (time, sourceUtc);
                break;
            case FlightEventType.OffBlock when Newer(OffBlockSourceUtc, sourceUtc):
                (OffBlockUtc, OffBlockSourceUtc) = (time, sourceUtc);
                break;
        }
    }

    private void Flag(FlightEventType type, bool value, DateTime sourceUtc)
    {
        // A status stated by a message (cancelled or not) holds until a more recent message states otherwise.
        if (type == FlightEventType.Cancelled && (CancelledSourceUtc is null || sourceUtc >= CancelledSourceUtc))
            (Cancelled, CancelledSourceUtc) = (value, sourceUtc);
        else if (type == FlightEventType.Diverted && (DivertedSourceUtc is null || sourceUtc >= DivertedSourceUtc))
            (Diverted, DivertedSourceUtc) = (value, sourceUtc);
    }

    // Every field a reader sees, so a message that repeats what is known changes nothing and raises nothing.
    private string Fingerprint() => string.Join('|', Carrier, Number, Suffix, ScheduledUtc.Ticks, Origin, Destination, Terminal, Stand, Gate, AircraftType, Seats,
        PaxEstimate, CodeshareCodes, EstimatedUtc?.Ticks, ActualUtc?.Ticks, OnBlockUtc?.Ticks, GateOpenUtc?.Ticks, BoardingStartUtc?.Ticks, OffBlockUtc?.Ticks,
        Cancelled, Diverted);

    private bool Changed(string before, string feed, DateTime receivedUtc)
    {
        Status = StatusOf(this);
        if (Fingerprint() == before)
            return false;
        Feed = FlightRules.NormalizeFeed(feed) ?? throw new ArgumentException("A feed name is 2 to 32 lower case letters, digits or hyphens.", nameof(feed));
        UpdatedUtc = receivedUtc;
        RaiseDomainEvent(new FlightChanged
        {
            SiteCode = SiteCode,
            FlightKey = FlightKey,
            Direction = Direction.ToString(),
            Carrier = Carrier,
            Number = Number,
            Suffix = Suffix,
            ScheduledUtc = ScheduledUtc,
            EstimatedUtc = EstimatedUtc,
            ActualUtc = ActualUtc,
            OnBlockUtc = OnBlockUtc,
            OffBlockUtc = OffBlockUtc,
            Status = Status.ToString(),
            Terminal = Terminal,
            Stand = Stand,
            Gate = Gate,
            Seats = Seats,
            PaxEstimate = PaxEstimate,
            OccurredOn = receivedUtc
        });
        return true;
    }

    private static void RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Message times are UTC.", name);
    }
}

/// <summary>
/// One flight milestone as a feed reported it (ARV-041): the record of what Ariva was told and when, whether or not it
/// changed the leg (an older message than the leg's milestone does not). Written once, never changed.
/// </summary>
public class FlightEvent : EntityBase<FlightEvent>, ISiteBound
{
    protected FlightEvent()
    {
    }

    public FlightEvent(FlightLeg leg, FlightEventType type, DateTime timeUtc, string feed, DateTime sourceUtc, DateTime receivedUtc, bool applied)
    {
        ArgumentNullException.ThrowIfNull(leg);
        Id = NewId();
        FlightLegId = leg.Id ?? throw new ArgumentException("The leg must be saved.", nameof(leg));
        SiteCode = leg.SiteCode;
        FlightKey = leg.FlightKey;
        Type = type;
        TimeUtc = timeUtc;
        Feed = FlightRules.NormalizeFeed(feed) ?? throw new ArgumentException("A feed name is 2 to 32 lower case letters, digits or hyphens.", nameof(feed));
        SourceUtc = sourceUtc;
        ReceivedUtc = receivedUtc;
        Applied = applied;
    }

    public virtual Guid FlightLegId { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual string FlightKey { get; protected set; }
    public virtual FlightEventType Type { get; protected set; }
    public virtual DateTime TimeUtc { get; protected set; }
    public virtual string Feed { get; protected set; }
    public virtual DateTime SourceUtc { get; protected set; }
    public virtual DateTime ReceivedUtc { get; protected set; }
    public virtual bool Applied { get; protected set; }
}

/// <summary>
/// The check-in counters a departing leg has at one check-in checkpoint (ARV-041). The feed's counter codes resolve to
/// Ariva desks through the AODB desk code mappings (ARV-015); codes that do not resolve are kept apart, never guessed
/// (<see cref="UnresolvedCodes"/>). Replaced by a message at least as recent as the one that set it.
/// </summary>
public class CounterAllocation : EntityBase<CounterAllocation>, ISiteBound
{
    protected CounterAllocation()
    {
    }

    public CounterAllocation(FlightLeg leg, string checkpointCode)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (leg.Direction != FlightDirection.Departure)
            throw new InvalidOperationException("Check-in counters are allocated to departures.");
        Id = NewId();
        FlightLegId = leg.Id ?? throw new ArgumentException("The leg must be saved.", nameof(leg));
        SiteCode = leg.SiteCode;
        FlightKey = leg.FlightKey;
        CheckpointCode = TopologyCodes.IsValid(checkpointCode) ? checkpointCode : throw new ArgumentException("A checkpoint code.", nameof(checkpointCode));
    }

    public virtual Guid FlightLegId { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual string FlightKey { get; protected set; }
    public virtual string CheckpointCode { get; protected set; }

    /// <summary>Ariva desk codes of the allocated counters, space separated.</summary>
    public virtual string DeskCodes { get; protected set; }

    /// <summary>The feed's counter codes no AODB desk code mapping of the checkpoint resolves, space separated.</summary>
    public virtual string UnresolvedCodes { get; protected set; }

    public virtual DateTime OpenUtc { get; protected set; }
    public virtual DateTime CloseUtc { get; protected set; }
    public virtual string HandlerCode { get; protected set; }
    public virtual string Feed { get; protected set; }
    public virtual DateTime? SourceUtc { get; protected set; }
    public virtual DateTime UpdatedUtc { get; protected set; }

    public virtual IReadOnlyList<string> Desks => string.IsNullOrEmpty(DeskCodes) ? [] : DeskCodes.Split(' ');

    /// <summary>
    /// Takes the allocation from a message of <paramref name="sourceUtc"/>, with the counters resolved to
    /// <paramref name="desks"/> and the rest <paramref name="unresolved"/>; true when it changed. An older message changes nothing.
    /// </summary>
    public virtual bool Apply(CounterAllocationValues values, IReadOnlyList<string> desks, IReadOnlyList<string> unresolved, string feed, DateTime sourceUtc,
        DateTime receivedUtc)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(desks);
        ArgumentNullException.ThrowIfNull(unresolved);
        if (!string.Equals(values.CheckpointCode, CheckpointCode, StringComparison.Ordinal) || !string.Equals(values.FlightKey, FlightKey, StringComparison.Ordinal))
            throw new ArgumentException("The values are of another flight or checkpoint.", nameof(values));
        if (SourceUtc is { } known && sourceUtc < known)
            return false;
        if (desks.Count + unresolved.Count == 0 || desks.Count + unresolved.Count > FlightRules.MaxCounters)
            throw new ArgumentException("An allocation has 1 to 100 counters.", nameof(desks));
        var before = (DeskCodes, UnresolvedCodes, OpenUtc, CloseUtc, HandlerCode);
        DeskCodes = desks.Count == 0 ? null : string.Join(' ', desks);
        UnresolvedCodes = unresolved.Count == 0 ? null : string.Join(' ', unresolved);
        (OpenUtc, CloseUtc, HandlerCode) = (values.OpenUtc, values.CloseUtc, values.HandlerCode);
        SourceUtc = sourceUtc;
        if (before == (DeskCodes, UnresolvedCodes, OpenUtc, CloseUtc, HandlerCode))
            return false;
        Feed = FlightRules.NormalizeFeed(feed) ?? throw new ArgumentException("A feed name is 2 to 32 lower case letters, digits or hyphens.", nameof(feed));
        UpdatedUtc = receivedUtc;
        return true;
    }
}

/// <summary>
/// How fresh one flight feed of a site is (ARV-041, runbook 4.3): when Ariva last heard from it, and the state the
/// stale-feed rule (<see cref="FeedFreshnessRule"/>) gave at the last sweep. Stale is the stale-feed alarm.
/// </summary>
public class FeedFreshness : EntityBase<FeedFreshness>, ISiteBound
{
    protected FeedFreshness()
    {
    }

    public virtual string SiteCode { get; protected set; }
    public virtual string Feed { get; protected set; }
    public virtual DateTime LastMessageUtc { get; protected set; }
    public virtual FeedState State { get; protected set; }
    public virtual DateTime StateSinceUtc { get; protected set; }

    /// <summary>Flights due at the site at the last sweep.</summary>
    public virtual int FlightsDue { get; protected set; }

    public virtual DateTime? SweptUtc { get; protected set; }

    /// <summary>Takes the rule's verdict at a sweep; the previous state when it changed, else null.</summary>
    public virtual FeedState? Assess(FeedState state, int flightsDue, DateTime now)
    {
        if (!Enum.IsDefined(state) || flightsDue < 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        FlightsDue = flightsDue;
        SweptUtc = now;
        if (state == State)
            return null;
        var previous = State;
        (State, StateSinceUtc) = (state, now);
        return previous;
    }
}
