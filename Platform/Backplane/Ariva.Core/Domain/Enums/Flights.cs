namespace Ariva.Core.Domain.Enums;

/// <summary>Whether a flight leg arrives at or departs from the site (ARV-041).</summary>
public enum FlightDirection
{
    Arrival,
    Departure
}

/// <summary>
/// A milestone of a flight leg an AODB reports (wiki 08, <c>POST /api/v1/flights/events</c>): the time it happened, or
/// for <see cref="Estimated"/> the estimated in-block (arrival) or off-block (departure) time.
/// </summary>
public enum FlightEventType
{
    Estimated,
    Landed,
    OnBlock,
    GateOpen,
    BoardingStart,
    OffBlock,
    Cancelled,
    Diverted
}

/// <summary>
/// Where a flight leg is, derived from the milestones known, never from the order messages arrived in: cancelled or
/// diverted first, then the furthest milestone reached.
/// </summary>
public enum FlightStatus
{
    Scheduled,
    Estimated,
    Landed,
    OnBlock,
    GateOpen,
    Boarding,
    OffBlock,
    Cancelled,
    Diverted
}

/// <summary>A flight feed's freshness (ARV-041, runbook 4.3).</summary>
public enum FeedState
{
    /// <summary>Messages arrive within the feed's cadence.</summary>
    Fresh,

    /// <summary>Silent, but no flight is due, so silence is expected (nights, a closed terminal).</summary>
    Idle,

    /// <summary>Silent beyond its cadence while flights are due: the stale-feed alarm.</summary>
    Stale
}
