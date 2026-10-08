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

    /// <summary>
    /// Alerts (ARV-039): every operational role sees and acts on the alerts its role owns (the service narrows each alert
    /// to its owner and escalation roles).
    /// </summary>
    private static readonly Permission[] Alerts = [Permissions.ViewAlert, Permissions.SearchAlert, Permissions.EditAlert];

    /// <summary>
    /// Passenger displays (ARV-058): the duty manager sets them up airport-side, the border shift supervisor in the
    /// immigration halls (a border deployment has no duty manager); handlers do not.
    /// </summary>
    private static readonly Permission[] Displays =
    [
        Permissions.ViewDisplay, Permissions.SearchDisplay, Permissions.CreateDisplay, Permissions.EditDisplay, Permissions.DeleteDisplay
    ];

    /// <summary>
    /// Reports (ARV-060): the daily report and its schedules, for the supervisor and the duty manager of the site; the
    /// handler's own-counter report comes with its module (Phase 1).
    /// </summary>
    private static readonly Permission[] Reports =
    [
        Permissions.ViewReport, Permissions.ViewReportSchedule, Permissions.SearchReportSchedule, Permissions.CreateReportSchedule,
        Permissions.EditReportSchedule, Permissions.DeleteReportSchedule
    ];

    /// <summary>
    /// Data quality (ARV-114a): the zone health checks of F18, for the supervisor and the duty manager who look after the
    /// site's zones and sensors (they hold ZonesAndDevices and the daily report); zone-level counts only, no desk data, so
    /// both see every queue zone of their sites, as in the daily report. Handlers do not: they see their own counters only.
    /// </summary>
    private static readonly Permission[] DataQuality = [Permissions.ViewDataQuality];

    /// <summary>
    /// Validation campaigns (ARV-104a): the supervisor and the duty manager who look after the site's zones and sensors see and
    /// run them (create, start, close with step-up). They do not capture: the ground truth comes from observers (separation of
    /// duties, owner decision 2026-10-08). An account that also holds <see cref="RoleCodes.ValidationObserver"/> may count, but
    /// never for a campaign it created or started (the capture service and script 0047 refuse it). Handlers do not.
    /// </summary>
    private static readonly Permission[] ValidationRun = [Permissions.ViewValidation, Permissions.ManageValidation];

    /// <summary>Border shift supervisor: immigration halls, their zones and sensors, the arrival wave, and the alert rules of its sites.</summary>
    public static IReadOnlySet<Permission> BorderShiftSupervisor { get; } = new HashSet<Permission>(
        [.. TopologyRead, .. ZonesAndDevices, Permissions.ViewLiveQueue, Permissions.ViewArrivalWave, Permissions.ViewArrivalWaveLanes, Permissions.ViewBorderDesks, Permissions.ViewImmigration, .. AlertRulesWrite, .. Alerts, .. Displays, .. Reports, .. DataQuality, .. ValidationRun]);

    /// <summary>
    /// Terminal duty manager: everything airport-side, including zones, sensors and desk code mappings; the arrival wave as
    /// flight and minute totals (no lane split, which is border data).
    /// </summary>
    public static IReadOnlySet<Permission> TerminalDutyManager { get; } = new HashSet<Permission>(
    [
        .. TopologyRead, .. ZonesAndDevices,
        Permissions.ViewDeskCodeMapping, Permissions.SearchDeskCodeMapping,
        Permissions.ViewLiveQueue, Permissions.ViewArrivalWave, Permissions.ViewAirportDesks, Permissions.ViewImmigration, .. AlertRulesWrite, .. Alerts,
        .. Displays, .. Reports, .. DataQuality, .. ValidationRun
    ]);

    /// <summary>Handler station manager: its own counters; reads topology and zones, changes none of them.</summary>
    public static IReadOnlySet<Permission> HandlerStationManager { get; } = new HashSet<Permission>(
    [
        .. TopologyRead,
        Permissions.ViewZoneProfile, Permissions.SearchZoneProfile,
        Permissions.ViewLiveQueue, Permissions.ViewAlertRule, Permissions.SearchAlertRule, .. Alerts
    ]);

    /// <summary>
    /// What the "every permission" rule of <see cref="SystemAdministrator"/> leaves out besides writing audit entries (declared
    /// before it: static fields initialise in order). Validation capture (ARV-104a, owner decision 2026-10-08): the ground truth
    /// a site's numbers are judged against stays independent of anyone who configures the system (zones, sensors, profiles,
    /// accounts), so only <see cref="RoleCodes.ValidationObserver"/> captures.
    /// </summary>
    private static readonly Permission[] NotForAdministrators = [Permissions.CaptureValidation];

    /// <summary>
    /// System administrator: configuration, users, integrations and the audit log, and every other permission (live
    /// queues and alerts included: administrators see and act on every alert of their sites, ARV-039), except
    /// <see cref="NotForAdministrators"/>: an administrator does not capture validation counts (one who must enter a paper
    /// sheet is given the Validation observer role for it like anyone else, and still never counts for a campaign it created
    /// or started). Audit entries are read-only for everyone.
    /// </summary>
    public static IReadOnlySet<Permission> SystemAdministrator { get; } = new HashSet<Permission>(
        Permissions.All.Values.Where(p => (p.Entity != "AuditEntry" || p.Action is PermissionAction.View or PermissionAction.Search) && !NotForAdministrators.Contains(p)));

    /// <summary>
    /// Validation observer (ARV-104a): captures manual counts for the running campaigns of its own sites (site access, ARV-012)
    /// and nothing else: no operational screen, no topology, no campaign management, no report.
    /// </summary>
    public static IReadOnlySet<Permission> ValidationObserver { get; } = new HashSet<Permission>([Permissions.CaptureValidation]);

    /// <summary>The seed by role code; every code in <see cref="RoleCodes.All"/> has an entry.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<Permission>> ByRole { get; } = new Dictionary<string, IReadOnlySet<Permission>>(StringComparer.Ordinal)
    {
        [RoleCodes.BorderShiftSupervisor] = BorderShiftSupervisor,
        [RoleCodes.TerminalDutyManager] = TerminalDutyManager,
        [RoleCodes.HandlerStationManager] = HandlerStationManager,
        [RoleCodes.SystemAdministrator] = SystemAdministrator,
        [RoleCodes.ValidationObserver] = ValidationObserver
    };

    /// <summary>The union of the permissions of the given roles; unknown role codes grant nothing.</summary>
    public static IReadOnlySet<Permission> For(IEnumerable<string> roleCodes) =>
        (roleCodes ?? []).Where(ByRole.ContainsKey).SelectMany(code => ByRole[code]).ToHashSet();
}
