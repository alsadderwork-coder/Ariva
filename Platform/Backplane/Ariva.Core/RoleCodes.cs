namespace Ariva.Core;

/// <summary>
/// Role codes recognised by Ariva. The values are stable identifiers carried in tokens, seed data and
/// configuration; never rename a value once it has shipped.
/// </summary>
public static class RoleCodes
{
    /// <summary>Border authority shift supervisor (Border module).</summary>
    public const string BorderShiftSupervisor = "BorderShiftSupervisor";

    /// <summary>Airport operator terminal duty manager (Airport Operations module).</summary>
    public const string TerminalDutyManager = "TerminalDutyManager";

    /// <summary>Ground handler station manager (Airport Operations module).</summary>
    public const string HandlerStationManager = "HandlerStationManager";

    /// <summary>System administrator: configuration, users and integrations.</summary>
    public const string SystemAdministrator = "SystemAdministrator";

    /// <summary>All role codes, in declaration order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        BorderShiftSupervisor,
        TerminalDutyManager,
        HandlerStationManager,
        SystemAdministrator
    ];
}
