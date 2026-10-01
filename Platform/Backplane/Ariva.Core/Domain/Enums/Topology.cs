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

/// <summary>The lifecycle of a zone profile version (ARV-016); stored by name.</summary>
public enum ZoneProfileStatus
{
    Draft,
    Published,
    Retired
}

/// <summary>The role of a zone in a process (glossary Zone); stored by name.</summary>
public enum ZoneKind
{
    /// <summary>Where people wait (a snake queue); owns the process.</summary>
    Queue,

    /// <summary>In front of a desk, where a passenger is served.</summary>
    Service,

    /// <summary>Behind a desk, where the officer or agent sits.</summary>
    Staff,

    /// <summary>Where the queue spills over outside the snake (overflow band).</summary>
    Overflow
}

/// <summary>The role of a line (glossary Entry line, Exit line, Count line, Overflow band); stored by name.</summary>
public enum LineRole
{
    Entry,
    Exit,
    Count,
    OverflowEntry
}
