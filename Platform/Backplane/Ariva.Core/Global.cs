using System.Reflection;
using Ariva.Core.Security;

namespace Ariva.Core;

/// <summary>
/// Platform wide definitions, following AMAN's Global.cs. Permissions live in <see cref="Defaults.Permissions"/> as
/// static properties so controllers write <c>[Permission(nameof(Global.Defaults.Permissions.ViewDesk))]</c> and a
/// misspelt name fails to compile.
/// </summary>
public static class Global
{
    public static class Defaults
    {
        /// <summary>
        /// Every permission (ARV-009): View, Create, Edit, Search and Delete for each entity, plus the few special
        /// actions. Entities are added by the stories that introduce their endpoints, together with the role grants in
        /// <see cref="RolePermissions"/> and the rows in security/permission-matrix.json.
        /// </summary>
        public static class Permissions
        {
            #region Administration

            public static Permission ViewSystemInfo { get; } = new("SystemInfo", PermissionAction.View);

            public static Permission ViewUser { get; } = new("User", PermissionAction.View);
            public static Permission CreateUser { get; } = new("User", PermissionAction.Create);
            public static Permission EditUser { get; } = new("User", PermissionAction.Edit);
            public static Permission SearchUser { get; } = new("User", PermissionAction.Search);
            public static Permission DeleteUser { get; } = new("User", PermissionAction.Delete);

            public static Permission ViewRole { get; } = new("Role", PermissionAction.View);
            public static Permission CreateRole { get; } = new("Role", PermissionAction.Create);
            public static Permission EditRole { get; } = new("Role", PermissionAction.Edit);
            public static Permission SearchRole { get; } = new("Role", PermissionAction.Search);
            public static Permission DeleteRole { get; } = new("Role", PermissionAction.Delete);

            public static Permission ViewAuditEntry { get; } = new("AuditEntry", PermissionAction.View);
            public static Permission CreateAuditEntry { get; } = new("AuditEntry", PermissionAction.Create);
            public static Permission EditAuditEntry { get; } = new("AuditEntry", PermissionAction.Edit);
            public static Permission SearchAuditEntry { get; } = new("AuditEntry", PermissionAction.Search);
            public static Permission DeleteAuditEntry { get; } = new("AuditEntry", PermissionAction.Delete);

            public static Permission ViewIntegrationClient { get; } = new("IntegrationClient", PermissionAction.View);
            public static Permission CreateIntegrationClient { get; } = new("IntegrationClient", PermissionAction.Create);
            public static Permission EditIntegrationClient { get; } = new("IntegrationClient", PermissionAction.Edit);
            public static Permission SearchIntegrationClient { get; } = new("IntegrationClient", PermissionAction.Search);
            public static Permission DeleteIntegrationClient { get; } = new("IntegrationClient", PermissionAction.Delete);

            // ARV-046: SSIM schedule files previewed and imported for a site. Only Create is used (a schedule is applied, not
            // kept); the other four exist because every catalogued entity has all five (PermissionModelTests).
            public static Permission ViewFlightSchedule { get; } = new("FlightSchedule", PermissionAction.View);
            public static Permission CreateFlightSchedule { get; } = new("FlightSchedule", PermissionAction.Create);
            public static Permission EditFlightSchedule { get; } = new("FlightSchedule", PermissionAction.Edit);
            public static Permission SearchFlightSchedule { get; } = new("FlightSchedule", PermissionAction.Search);
            public static Permission DeleteFlightSchedule { get; } = new("FlightSchedule", PermissionAction.Delete);

            #endregion

            #region Topology (E1)

            public static Permission ViewSite { get; } = new("Site", PermissionAction.View);
            public static Permission CreateSite { get; } = new("Site", PermissionAction.Create);
            public static Permission EditSite { get; } = new("Site", PermissionAction.Edit);
            public static Permission SearchSite { get; } = new("Site", PermissionAction.Search);
            public static Permission DeleteSite { get; } = new("Site", PermissionAction.Delete);

            public static Permission ViewAirport { get; } = new("Airport", PermissionAction.View);
            public static Permission CreateAirport { get; } = new("Airport", PermissionAction.Create);
            public static Permission EditAirport { get; } = new("Airport", PermissionAction.Edit);
            public static Permission SearchAirport { get; } = new("Airport", PermissionAction.Search);
            public static Permission DeleteAirport { get; } = new("Airport", PermissionAction.Delete);

            public static Permission ViewTerminal { get; } = new("Terminal", PermissionAction.View);
            public static Permission CreateTerminal { get; } = new("Terminal", PermissionAction.Create);
            public static Permission EditTerminal { get; } = new("Terminal", PermissionAction.Edit);
            public static Permission SearchTerminal { get; } = new("Terminal", PermissionAction.Search);
            public static Permission DeleteTerminal { get; } = new("Terminal", PermissionAction.Delete);

            public static Permission ViewLevel { get; } = new("Level", PermissionAction.View);
            public static Permission CreateLevel { get; } = new("Level", PermissionAction.Create);
            public static Permission EditLevel { get; } = new("Level", PermissionAction.Edit);
            public static Permission SearchLevel { get; } = new("Level", PermissionAction.Search);
            public static Permission DeleteLevel { get; } = new("Level", PermissionAction.Delete);

            public static Permission ViewCheckpoint { get; } = new("Checkpoint", PermissionAction.View);
            public static Permission CreateCheckpoint { get; } = new("Checkpoint", PermissionAction.Create);
            public static Permission EditCheckpoint { get; } = new("Checkpoint", PermissionAction.Edit);
            public static Permission SearchCheckpoint { get; } = new("Checkpoint", PermissionAction.Search);
            public static Permission DeleteCheckpoint { get; } = new("Checkpoint", PermissionAction.Delete);

            public static Permission ViewDesk { get; } = new("Desk", PermissionAction.View);
            public static Permission CreateDesk { get; } = new("Desk", PermissionAction.Create);
            public static Permission EditDesk { get; } = new("Desk", PermissionAction.Edit);
            public static Permission SearchDesk { get; } = new("Desk", PermissionAction.Search);
            public static Permission DeleteDesk { get; } = new("Desk", PermissionAction.Delete);

            public static Permission ViewDeskCodeMapping { get; } = new("DeskCodeMapping", PermissionAction.View);
            public static Permission CreateDeskCodeMapping { get; } = new("DeskCodeMapping", PermissionAction.Create);
            public static Permission EditDeskCodeMapping { get; } = new("DeskCodeMapping", PermissionAction.Edit);
            public static Permission SearchDeskCodeMapping { get; } = new("DeskCodeMapping", PermissionAction.Search);
            public static Permission DeleteDeskCodeMapping { get; } = new("DeskCodeMapping", PermissionAction.Delete);

            public static Permission ViewZoneProfile { get; } = new("ZoneProfile", PermissionAction.View);
            public static Permission CreateZoneProfile { get; } = new("ZoneProfile", PermissionAction.Create);
            public static Permission EditZoneProfile { get; } = new("ZoneProfile", PermissionAction.Edit);
            public static Permission SearchZoneProfile { get; } = new("ZoneProfile", PermissionAction.Search);
            public static Permission DeleteZoneProfile { get; } = new("ZoneProfile", PermissionAction.Delete);
            public static Permission PublishZoneProfile { get; } = new("ZoneProfile", PermissionAction.Publish);

            public static Permission ViewFloorPlan { get; } = new("FloorPlan", PermissionAction.View);
            public static Permission CreateFloorPlan { get; } = new("FloorPlan", PermissionAction.Create);
            public static Permission EditFloorPlan { get; } = new("FloorPlan", PermissionAction.Edit);
            public static Permission SearchFloorPlan { get; } = new("FloorPlan", PermissionAction.Search);
            public static Permission DeleteFloorPlan { get; } = new("FloorPlan", PermissionAction.Delete);

            public static Permission ViewDevice { get; } = new("Device", PermissionAction.View);
            public static Permission CreateDevice { get; } = new("Device", PermissionAction.Create);
            public static Permission EditDevice { get; } = new("Device", PermissionAction.Edit);
            public static Permission SearchDevice { get; } = new("Device", PermissionAction.Search);
            public static Permission DeleteDevice { get; } = new("Device", PermissionAction.Delete);

            #endregion

            #region Live operations (E4)

            /// <summary>Join a zone's live state on the hub (ARV-035): queue length and nowcast as they change.</summary>
            public static Permission ViewLiveQueue { get; } = new("LiveQueue", PermissionAction.View);

            /// <summary>The arrival-wave projection of a site (ARV-047): flights landing soon and the hall arrivals they bring.</summary>
            public static Permission ViewArrivalWave { get; } = new("ArrivalWave", PermissionAction.View);

            /// <summary>
            /// The arrival wave's split by lane category and e-gate eligibility (ARV-047): border data, so border roles see it;
            /// airport roles see flight and minute totals only (wiki 01, 12).
            /// </summary>
            public static Permission ViewArrivalWaveLanes { get; } = new("ArrivalWaveLanes", PermissionAction.View);

            /// <summary>
            /// The states of immigration and emigration desks and e-gates on the live screen (ARV-055): border data, so border
            /// roles see them; airport roles never do (wiki 01, 12).
            /// </summary>
            public static Permission ViewBorderDesks { get; } = new("BorderDesks", PermissionAction.View);

            /// <summary>
            /// The states of every check-in counter and security lane of a site on the live screen (ARV-055): the terminal duty
            /// manager's view. A handler sees only its own counters (wiki 01), which needs handler tenancy, so handlers do not
            /// hold this until then; border roles do not see check-in data.
            /// </summary>
            public static Permission ViewAirportDesks { get; } = new("AirportDesks", PermissionAction.View);

            /// <summary>
            /// The immigration screen (ARV-057): lane waits, lane aggregates of the border desks and e-gate totals for border
            /// roles and terminal duty managers; per-desk and per-gate figures also need <see cref="ViewBorderDesks"/>.
            /// </summary>
            public static Permission ViewImmigration { get; } = new("Immigration", PermissionAction.View);

            #endregion

            #region Alerting (E5)

            public static Permission ViewAlertRule { get; } = new("AlertRule", PermissionAction.View);
            public static Permission CreateAlertRule { get; } = new("AlertRule", PermissionAction.Create);
            public static Permission EditAlertRule { get; } = new("AlertRule", PermissionAction.Edit);
            public static Permission SearchAlertRule { get; } = new("AlertRule", PermissionAction.Search);
            public static Permission DeleteAlertRule { get; } = new("AlertRule", PermissionAction.Delete);

            /// <summary>Alerts (ARV-039): see them; act on them (acknowledge, escalate, resolve) where the caller's role owns them.</summary>
            public static Permission ViewAlert { get; } = new("Alert", PermissionAction.View);
            public static Permission SearchAlert { get; } = new("Alert", PermissionAction.Search);
            public static Permission EditAlert { get; } = new("Alert", PermissionAction.Edit);

            #endregion

            #region Passenger displays (E8)

            /// <summary>Passenger displays (ARV-058): the boards' settings and their players' credentials.</summary>
            public static Permission ViewDisplay { get; } = new("Display", PermissionAction.View);
            public static Permission SearchDisplay { get; } = new("Display", PermissionAction.Search);
            public static Permission CreateDisplay { get; } = new("Display", PermissionAction.Create);
            public static Permission EditDisplay { get; } = new("Display", PermissionAction.Edit);
            public static Permission DeleteDisplay { get; } = new("Display", PermissionAction.Delete);

            #endregion

            private static readonly Lazy<IReadOnlyDictionary<string, Permission>> ByName = new(() =>
                typeof(Permissions)
                    .GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Where(property => property.PropertyType == typeof(Permission))
                    .ToDictionary(property => property.Name, property => (Permission)property.GetValue(null), StringComparer.Ordinal));

            /// <summary>Every permission, keyed by property name.</summary>
            public static IReadOnlyDictionary<string, Permission> All => ByName.Value;

            /// <summary>The permission for a property name, or null when no such permission exists.</summary>
            public static Permission Find(string name) =>
                name is not null && ByName.Value.TryGetValue(name, out var permission) ? permission : null;
        }
    }
}
