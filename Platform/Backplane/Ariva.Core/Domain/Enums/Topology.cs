namespace Ariva.Core.Domain.Enums;

/// <summary>The process a checkpoint runs (ARV-013); stored by name.</summary>
public enum CheckpointKind
{
    CheckIn,
    Security,
    Emigration,
    Immigration
}

/// <summary>The kind of service point (glossary Desk); stored by name. Which kinds a checkpoint allows depends on its kind.</summary>
public enum DeskKind
{
    /// <summary>A check-in counter.</summary>
    Counter,

    /// <summary>A security screening lane.</summary>
    SecurityLane,

    /// <summary>A manned emigration or immigration desk.</summary>
    Desk,

    /// <summary>An automated border gate; rejects go to a manual lane.</summary>
    EGate
}

/// <summary>A system whose own desk, gate or counter codes are mapped to Ariva desks (ARV-015); stored by name.</summary>
public enum ExternalSystem
{
    /// <summary>AMAN border management: desk and e-gate codes on the aman.feed topics.</summary>
    Aman,

    /// <summary>The airport operational database: check-in counter codes in allocations.</summary>
    Aodb
}
