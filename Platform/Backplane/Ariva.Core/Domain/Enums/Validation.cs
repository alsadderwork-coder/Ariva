namespace Ariva.Core.Domain.Enums;

/// <summary>The lifecycle of a validation campaign (ARV-104a); stored by name.</summary>
public enum ValidationCampaignStatus
{
    /// <summary>Created: its profile version, scope, days and targets are fixed; nothing is captured yet.</summary>
    Planned,

    /// <summary>Started: observers record manual counts for its lines and planned days.</summary>
    Running,

    /// <summary>Closed (a step-up critical action): nothing is captured or corrected any more.</summary>
    Closed
}

/// <summary>
/// The state of a border desk an observer records for one minute of a validation campaign (ARV-104b, F10, F18): the four
/// states of formulas F10 a person can see. Unknown is the engine's answer to missing signals, never an observation. Stored
/// by name.
/// </summary>
public enum ObservedDeskState
{
    /// <summary>Nobody at the desk, or the desk shut.</summary>
    Closed,

    /// <summary>Staffed and open, nobody being served.</summary>
    Idle,

    /// <summary>A traveller being processed.</summary>
    Serving,

    /// <summary>Staffed but not processing (a break, a supervisor's question): not open for throughput.</summary>
    Paused
}
