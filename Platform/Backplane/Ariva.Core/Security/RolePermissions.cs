using static Ariva.Core.Global.Defaults;

namespace Ariva.Core.Security;

/// <summary>
/// The role to permission seed (ARV-009), from the role descriptions in wiki/01-Product-Overview.md. ARV-011 stores
/// these grants in the database and lets administrators adjust them; this seed stays the default for a new
/// deployment. Which sites a user sees is a separate filter (ARV-012), never a role.
/// </summary>
public static class RolePermissions
{
    private static readonly Permission[] TopologyRead =
    [
        Permissions.ViewSite, Permissions.SearchSite,
        Permissions.ViewAirport, Permissions.SearchAirport,
        Permissions.ViewTerminal, Permissions.SearchTerminal,
        Permissions.ViewLevel, Permissions.SearchLevel,
        Permissions.ViewCheckpoint, Permissions.SearchCheckpoint,
        Permissions.ViewDesk, Permissions.SearchDesk,
        Permissions.ViewFloorPlan, Permissions.SearchFloorPlan
    ];

    private static readonly Permission[] ZonesAndDevices =
    [
        Permissions.ViewZoneProfile, Permissions.SearchZoneProfile, Permissions.CreateZoneProfile,
        Permissions.EditZoneProfile, Permissions.PublishZoneProfile,
        Permissions.ViewDevice, Permissions.SearchDevice, Permissions.CreateDevice, Permissions.EditDevice
    ];

    /// <summary>Alert rules (ARV-037): the supervisors and duty managers who own alerts write the rules; handlers read them.</summary>
    private static readonly Permission[] AlertRulesWrite =
    [
        Permissions.ViewAlertRule, Permissions.SearchAlertRule, Permissions.CreateAlertRule, Permissions.EditAlertRule, Permissions.DeleteAlertRule
    ];

    /// <summary>Border shift supervisor: immigration halls, their zones and sensors, and the alert rules of its sites.</summary>
    public static IReadOnlySet<Permission> BorderShiftSupervisor { get; } = new HashSet<Permission>([.. TopologyRead, .. ZonesAndDevices, Permissions.ViewLiveQueue, .. AlertRulesWrite]);

    /// <summary>Terminal duty manager: everything airport-side, including zones, sensors and desk code mappings.</summary>
    public static IReadOnlySet<Permission> TerminalDutyManager { get; } = new HashSet<Permission>(
    [
        .. TopologyRead, .. ZonesAndDevices,
        Permissions.ViewDeskCodeMapping, Permissions.SearchDeskCodeMapping,
        Permissions.ViewLiveQueue, .. AlertRulesWrite
    ]);

    /// <summary>Handler station manager: its own counters; reads topology and zones, changes none of them.</summary>
    public static IReadOnlySet<Permission> HandlerStationManager { get; } = new HashSet<Permission>(
    [
        .. TopologyRead,
        Permissions.ViewZoneProfile, Permissions.SearchZoneProfile,
        Permissions.ViewLiveQueue, Permissions.ViewAlertRule, Permissions.SearchAlertRule
    ]);

    /// <summary>
    /// System administrator: configuration, users, integrations and the audit log; no operational data (live queues,
    /// alerts) beyond what configuration needs. Audit entries are read-only for everyone.
    /// </summary>
    public static IReadOnlySet<Permission> SystemAdministrator { get; } = new HashSet<Permission>(
        Permissions.All.Values.Where(p => p.Entity != "AuditEntry" || p.Action is PermissionAction.View or PermissionAction.Search));

    /// <summary>The seed by role code; every code in <see cref="RoleCodes.All"/> has an entry.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<Permission>> ByRole { get; } = new Dictionary<string, IReadOnlySet<Permission>>(StringComparer.Ordinal)
    {
        [RoleCodes.BorderShiftSupervisor] = BorderShiftSupervisor,
        [RoleCodes.TerminalDutyManager] = TerminalDutyManager,
        [RoleCodes.HandlerStationManager] = HandlerStationManager,
        [RoleCodes.SystemAdministrator] = SystemAdministrator
    };

    /// <summary>The union of the permissions of the given roles; unknown role codes grant nothing.</summary>
    public static IReadOnlySet<Permission> For(IEnumerable<string> roleCodes) =>
        (roleCodes ?? []).Where(ByRole.ContainsKey).SelectMany(code => ByRole[code]).ToHashSet();
}
