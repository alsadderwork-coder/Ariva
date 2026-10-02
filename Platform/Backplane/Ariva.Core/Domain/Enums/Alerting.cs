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
    DesksBelowPlan
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
