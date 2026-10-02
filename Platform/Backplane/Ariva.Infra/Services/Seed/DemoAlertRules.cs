using Ariva.Core.Domain.Enums;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// The prototype's seeded alert rules R-001 to R-005 (docs/design/prototype/app/assets/sim.js, the scenario's
/// <c>ScenarioDay.SeedRules</c>) as Ariva rules for the demo airport (ARV-037). Owners and escalation targets the
/// prototype names as people outside Ariva's roles are escalation contacts; "zone" (the zone's owner) and "auto" (the
/// owner's escalation path) are left empty.
/// </summary>
public static class DemoAlertRules
{
    private static readonly string[] Immigration = ["A-CRW", "A-CIT", "A-RES", "A-VIS", "A-EG", "D-CRW", "D-CIT", "D-RES", "D-VIS", "D-EG"];
    private static readonly string[] AirportSide = ["CI-A", "CI-B", "CI-C", "CI-D", "SEC-N", "SEC-S"];
    private static readonly string[] Overflow = ["A-OV", "D-OV", "SEC-OV"];

    /// <summary>R-001 to R-005, in code order.</summary>
    public static IReadOnlyList<AlertRuleValues> Values { get; } =
    [
        new("Nowcast above 15 min", Immigration, AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, 10, 12, 1, 1, AlertSeverity.Critical,
            Ariva.Core.RoleCodes.BorderShiftSupervisor, 10, null, "Border operations duty officer", false, true),
        new("Overflow band occupied", [.. Immigration, .. AirportSide], AlertMetric.OverflowOccupied, AlertComparator.IsTrue, null, null, null, 3, 3, AlertSeverity.Warning,
            null, 15, null, null, false, true),
        new("Sensor offline", [.. Immigration, .. AirportSide, .. Overflow], AlertMetric.SensorOffline, AlertComparator.IsTrue, null, null, null, 1, 1, AlertSeverity.Warning,
            null, 15, null, "Systems", false, true),
        new("Check-in P90 above SLA threshold", ["CI-C", "CI-D"], AlertMetric.BinP90, AlertComparator.GreaterThan, 15, null, null, 1, 1, AlertSeverity.Critical,
            Ariva.Core.RoleCodes.HandlerStationManager, 15, Ariva.Core.RoleCodes.TerminalDutyManager, null, false, true),
        new("Nowcast above 15 min, airport side", AirportSide, AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, 10, 12, 1, 1, AlertSeverity.Critical,
            null, 10, null, null, false, true)
    ];
}
