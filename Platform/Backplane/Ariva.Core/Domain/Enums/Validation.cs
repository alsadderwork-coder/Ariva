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
