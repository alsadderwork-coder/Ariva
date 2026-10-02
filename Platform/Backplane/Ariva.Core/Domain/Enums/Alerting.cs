namespace Ariva.Core.Domain.Enums;

/// <summary>What an alert rule watches (ARV-037, Administration guide section 8); stored by name.</summary>
public enum AlertMetric
{
    /// <summary>The live nowcast wait of a queue zone, in minutes (F8).</summary>
    Nowcast,

    /// <summary>The realised P90 wait of a queue zone's 15-minute bin, in minutes (F7); informative, never a penalty decision.</summary>
    BinP90,

    /// <summary>People queuing in a queue zone.</summary>
    QueueLength,

    /// <summary>A queue zone's overflow band is occupied (true or false).</summary>
    OverflowOccupied,

    /// <summary>A device of the zone is offline (true or false).</summary>
    SensorOffline,

    /// <summary>Desks open below the accepted staffing plan, in desks.</summary>
    DesksBelowPlan,

    /// <summary>
    /// The highest nowcast wait a queue zone is projected to reach within the rule's lead time (15 to 60 minutes), in
    /// minutes: the queue now, stepped with the arrival-wave projection (F14, ARV-047) and what the desks clear at the
    /// current throughput (ARV-038). A predicted breach, not a measurement.
    /// </summary>
    PredictedNowcast
}

/// <summary>Where an alert is in its life (glossary Alert); ARV-038 raises and resolves, ARV-039 acknowledges and escalates.</summary>
public enum AlertState
{
    Raised,
    Acknowledged,
    Escalated,
    Resolved
}

/// <summary>Why an alert was resolved.</summary>
public enum AlertResolution
{
    /// <summary>The rule's clear condition held for its clear minutes.</summary>
    Cleared,

    /// <summary>The rule was disabled or deleted while the alert was open.</summary>
    RuleWithdrawn,

    /// <summary>The target left the rule (a zone removed from its scope, a device moved or decommissioned).</summary>
    TargetWithdrawn,

    /// <summary>The rule's metric changed while the alert was open: the alert was about something the rule no longer watches.</summary>
    RuleChanged,

    /// <summary>Someone resolved it (ARV-039).</summary>
    Manual
}

/// <summary>How a rule compares its metric with its threshold; <see cref="IsTrue"/> for the true-or-false metrics.</summary>
public enum AlertComparator
{
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    IsTrue
}

/// <summary>How urgent an alert of the rule is.</summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical
}
