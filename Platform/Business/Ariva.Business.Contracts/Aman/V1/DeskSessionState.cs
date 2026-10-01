namespace Ariva.Business.Contracts.Aman.V1;

/// <summary>
/// State of a border desk session as reported by AMAN. Numeric values are part of the contract and never
/// change; zero is deliberately unused so that a missing value is detectable.
/// </summary>
public enum DeskSessionState
{
    /// <summary>A desk session started (an officer signed in at the desk).</summary>
    Opened = 1,

    /// <summary>The desk session ended (the officer signed out or the session expired).</summary>
    Closed = 2,

    /// <summary>The desk is staffed but paused (for example a break with the session kept open).</summary>
    Paused = 3
}
