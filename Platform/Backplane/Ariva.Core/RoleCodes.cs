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

    /// <summary>
    /// Validation observer (ARV-104a, accuracy assurance): records manual counts for the running validation campaigns of
    /// its own sites and nothing else; no operational screen, no campaign management, no report.
    /// </summary>
    public const string ValidationObserver = "ValidationObserver";

    /// <summary>All role codes, in declaration order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        BorderShiftSupervisor,
        TerminalDutyManager,
        HandlerStationManager,
        SystemAdministrator,
        ValidationObserver
    ];

    /// <summary>
    /// The roles that own, are escalated to and receive alerts (ARV-037, ARV-039): every role but the validation observer,
    /// which holds no alert permission. The alert_rule table accepts exactly these (script 0020).
    /// </summary>
    public static readonly IReadOnlyList<string> AlertRoles =
    [
        BorderShiftSupervisor,
        TerminalDutyManager,
        HandlerStationManager,
        SystemAdministrator
    ];

    /// <summary>
    /// Whether an account holding exactly these roles must be bound to named sites and may never reach every site
    /// (ARV-104a, first security review, 2026-10-08): an account whose only role is <see cref="ValidationObserver"/>. An observer
    /// counts at the sites of its campaigns; every site would let it count wherever a campaign runs, now and at sites added
    /// later. An account that also holds another role follows that role's rules.
    /// </summary>
    public static bool NeedsNamedSites(IEnumerable<string> roles)
    {
        var held = (roles ?? []).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToList();
        return held.Count == 1 && held[0] == ValidationObserver;
    }
}
