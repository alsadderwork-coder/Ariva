namespace Ariva.Business.Contracts.Aman.V1;

/// <summary>
/// Published by AMAN when a border desk opens, closes or pauses. AMAN derives it from officer sign in and
/// sign out at the desk, but the contract carries the desk state only: no officer identifier crosses.
/// Ariva uses it as the login signal in the desk state machine.
/// </summary>
/// <param name="SiteCode">AMAN site code (the border point, for example an airport arrivals hall).</param>
/// <param name="DeskCode">AMAN desk code within the site; Ariva maps it to its own desk through the desk code mapping.</param>
/// <param name="State">The new desk session state.</param>
/// <param name="LaneCategory">Lane category the desk serves after the change, as an AMAN code mapped to Ariva lane categories; empty when closed.</param>
/// <param name="OccurredAtUtc">When the state change happened, in UTC, coarsened by AMAN to its publishing interval.</param>
/// <param name="SourceEventId">AMAN's event identifier, used by Ariva for idempotent consumption.</param>
public sealed record DeskSessionChanged(
    string SiteCode,
    string DeskCode,
    DeskSessionState State,
    string LaneCategory,
    DateTimeOffset OccurredAtUtc,
    string SourceEventId);
