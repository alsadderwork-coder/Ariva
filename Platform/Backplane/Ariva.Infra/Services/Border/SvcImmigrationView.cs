using Ariva.Core.Security;
using Ariva.Core.Services.Border;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Border;

/// <summary>
/// The immigration view (ARV-057, <see cref="ISvcImmigrationView"/>): AMAN's interval aggregates and session events as
/// the immigration intake stored them (ARV-048), for the desks and e-gates mapped to the site's Immigration (arrivals)
/// and Emigration (departures) checkpoints. Lane and e-gate totals for every caller with <c>Immigration.View</c>; per-desk
/// and per-gate rows only with <c>BorderDesks.View</c>, decided here so a duty manager's answer never holds a desk code.
/// Nothing about officers or travellers is stored, so nothing can be shown.
/// </summary>
internal sealed class SvcImmigrationView(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, CallerRoles callerRoles)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcImmigrationView
{
    private static readonly string[] HallKinds = ["Immigration", "Emigration"];
    private static readonly string[] LaneOrder = ["CIT", "RES", "VIS", "CRW"];

    /// <summary>A lane's service and cycle times are shown to callers without desk data from this many desks.</summary>
    internal const int MinDesksForTimes = 3;

    public async Task<Result<ImmigrationViewModel>> GetAsync(string siteCode, CancellationToken ct = default)
    {
        if (siteCode is null || !(await siteScope.GetAsync(ct)).Allows(siteCode) ||
            (await ExecuteSqlAsync<CodeRow>("""SELECT code AS "Code" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count == 0)
            return Result.Error<ImmigrationViewModel>(TopologyErrors.NotFound);

        var desksIncluded = RolePermissions.For(await callerRoles.GetAsync(ct)).Contains(Ariva.Core.Global.Defaults.Permissions.ViewBorderDesks);
        var now = UtcNow;
        var to = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var from = to.AddMinutes(-ISvcImmigrationView.WindowMinutes);
        var window = new Dictionary<string, object> { ["site"] = siteCode, ["from"] = from, ["to"] = to };

        var intervals = await ExecuteSqlAsync<DeskIntervalRow>(
            """
            SELECT c.kind AS "Kind", d.code AS "Desk", i.lane_category AS "Lane", i.interval_start_utc AS "Start",
                   i.transactions_processed AS "Transactions", i.mean_service_seconds AS "MeanService",
                   i.p90_service_seconds AS "P90Service", i.mean_cycle_seconds AS "MeanCycle"
              FROM border_desk_interval i
              JOIN desk d ON d.id = i.desk_id AND d.site_code = :site
              JOIN checkpoint c ON c.id = d.checkpoint_id
             WHERE i.site_code = :site AND i.interval_start_utc >= :from AND i.interval_start_utc < :to
               AND c.kind IN ('Immigration', 'Emigration') AND d.kind = 'Desk'
             ORDER BY i.interval_start_utc
             LIMIT 20000
            """, window, ct);

        // A desk's state is its latest session event of the last day; one never heard from is Unknown.
        var sessions = await ExecuteSqlAsync<SessionRow>(
            """
            SELECT DISTINCT ON (d.id) c.kind AS "Kind", d.code AS "Desk", s.state AS "State", s.lane_category AS "Lane"
              FROM border_desk_session s
              JOIN desk d ON d.id = s.desk_id AND d.site_code = :site
              JOIN checkpoint c ON c.id = d.checkpoint_id
             WHERE s.site_code = :site AND s.occurred_utc <= :to AND s.occurred_utc > :since
               AND c.kind IN ('Immigration', 'Emigration') AND d.kind = 'Desk'
             ORDER BY d.id, s.occurred_utc DESC
            """, new Dictionary<string, object> { ["site"] = siteCode, ["to"] = to, ["since"] = to.AddDays(-1) }, ct);

        var gates = await ExecuteSqlAsync<GateRow>(
            """
            SELECT c.kind AS "Kind", d.code AS "Gate", CAST(sum(g.attempts) AS integer) AS "Attempts", CAST(sum(g.accepted) AS integer) AS "Accepted", CAST(sum(g.rejected) AS integer) AS "Rejected",
                   CAST(sum(g.rejects_document_read) AS integer) AS "DocumentRead", CAST(sum(g.rejects_biometric_capture) AS integer) AS "BiometricCapture",
                   CAST(sum(g.rejects_eligibility) AS integer) AS "Eligibility", CAST(sum(g.rejects_referred_to_officer) AS integer) AS "ReferredToOfficer",
                   CAST(sum(g.rejects_technical) AS integer) AS "Technical", CAST(sum(g.rejects_other) AS integer) AS "Other",
                   CAST(sum(g.attempts * g.mean_cycle_seconds) AS double precision) AS "BusySeconds"
              FROM border_egate_interval g
              JOIN desk d ON d.id = g.desk_id AND d.site_code = :site
              JOIN checkpoint c ON c.id = d.checkpoint_id
             WHERE g.site_code = :site AND g.interval_start_utc >= :from AND g.interval_start_utc < :to
               AND c.kind IN ('Immigration', 'Emigration') AND d.kind = 'EGate'
             GROUP BY c.kind, d.code
            """, window, ct);

        var configured = await ExecuteSqlAsync<CountRow>(
            """
            SELECT c.kind AS "Kind", CAST(count(*) AS integer) AS "Count"
              FROM desk d JOIN checkpoint c ON c.id = d.checkpoint_id
             WHERE d.site_code = :site AND d.deleted_on IS NULL AND c.deleted_on IS NULL AND d.in_service
               AND d.kind = 'EGate' AND c.kind IN ('Immigration', 'Emigration')
             GROUP BY c.kind
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct);

        // The published profile's queue zones that are a lane's queue, on a level with the hall's checkpoint.
        var queues = await ExecuteSqlAsync<QueueRow>(
            """
            SELECT DISTINCT c.kind AS "Kind", z.name AS "Zone", z.lane_category AS "Lane"
              FROM zone z
              JOIN zone_profile p ON p.id = z.profile_id
              JOIN checkpoint c ON c.level_id = z.level_id AND c.deleted_on IS NULL
             WHERE p.site_code = :site AND p.status = 'Published' AND z.kind = 'Queue' AND z.lane_category IS NOT NULL
               AND c.kind IN ('Immigration', 'Emigration')
             ORDER BY z.name
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct);

        var halls = HallKinds
            .Select(kind => Hall(kind, intervals.Where(r => r.Kind == kind).ToList(), sessions.Where(r => r.Kind == kind).ToList(),
                gates.Where(r => r.Kind == kind).ToList(), configured.FirstOrDefault(r => r.Kind == kind)?.Count ?? 0, desksIncluded) with
            {
                Queues = [.. queues.Where(q => q.Kind == kind).Select(q => new ImmigrationQueueViewModel(Ariva.Core.Sensing.ZoneKeys.For(siteCode, q.Zone), q.Zone, q.Lane))]
            })
            .ToList();
        return new Result<ImmigrationViewModel>(new ImmigrationViewModel(siteCode, now, ISvcImmigrationView.WindowMinutes, desksIncluded, halls));
    }

    internal static ImmigrationHallViewModel Hall(string kind, IReadOnlyList<DeskIntervalRow> intervals, IReadOnlyList<SessionRow> sessions, IReadOnlyList<GateRow> gates,
        int gatesConfigured, bool desksIncluded)
    {
        var windowSeconds = ISvcImmigrationView.WindowMinutes * 60.0;
        var lanes = LaneOrder
            .Where(lane => intervals.Any(r => r.Lane == lane) || sessions.Any(s => s.Lane == lane))
            .Select(lane =>
            {
                var rows = intervals.Where(r => r.Lane == lane).ToList();
                var served = rows.Sum(r => r.Transactions);
                // A lane's times from fewer than three desks are one or two desks' own figures: for a caller without desk
                // data they are left out, as AMAN leaves small cells out (ARV-057).
                var shown = desksIncluded || rows.Where(r => r.Transactions > 0).Select(r => r.Desk).Distinct(StringComparer.Ordinal).Count() >= MinDesksForTimes;
                return new ImmigrationLaneViewModel(lane,
                    sessions.Count(s => s.Lane == lane && s.State == "Opened"),
                    sessions.Count(s => s.Lane == lane && s.State == "Paused"),
                    served,
                    shown ? Weighted(rows, r => r.MeanService) : null,
                    shown ? rows.Where(r => r.Transactions > 0).Select(r => (double?)r.P90Service).Max() : null,
                    shown ? Weighted(rows, r => r.MeanCycle) : null);
            })
            .ToList();

        var manual = Weighted(intervals, r => r.MeanService);
        var rejected = gates.Sum(g => g.Rejected);
        var attempts = gates.Sum(g => g.Attempts);
        var extraDeskMinutes = manual is { } seconds ? Math.Round(rejected * seconds / 60, 1) : 0;
        var open = sessions.Count(s => s.State == "Opened");
        var eGates = new ImmigrationEGatesViewModel(
            gatesConfigured,
            gates.Count(g => g.Attempts > 0),
            attempts,
            gates.Sum(g => g.Accepted),
            rejected,
            attempts > 0 ? Math.Round((double)rejected / attempts, 3) : null,
            gatesConfigured > 0 ? Math.Round(Math.Min(1, gates.Sum(g => g.BusySeconds) / (gatesConfigured * windowSeconds)), 3) : null,
            new ImmigrationRejectsViewModel(gates.Sum(g => g.DocumentRead), gates.Sum(g => g.BiometricCapture), gates.Sum(g => g.Eligibility),
                gates.Sum(g => g.ReferredToOfficer), gates.Sum(g => g.Technical), gates.Sum(g => g.Other)),
            extraDeskMinutes,
            open > 0 && manual is not null ? Math.Round(extraDeskMinutes / open, 1) : null);

        IReadOnlyList<ImmigrationDeskViewModel> desks = [];
        IReadOnlyList<ImmigrationGateViewModel> gateRows = [];
        if (desksIncluded)
        {
            var names = intervals.Select(r => r.Desk).Concat(sessions.Select(s => s.Desk)).Distinct(StringComparer.Ordinal);
            desks = names
                .Select(desk =>
                {
                    var rows = intervals.Where(r => r.Desk == desk).ToList();
                    var session = sessions.FirstOrDefault(s => s.Desk == desk);
                    return new ImmigrationDeskViewModel(desk,
                        session?.Lane ?? rows.LastOrDefault()?.Lane,
                        session?.State ?? "Unknown",
                        rows.Sum(r => r.Transactions),
                        Weighted(rows, r => r.MeanService),
                        rows.Where(r => r.Transactions > 0).Select(r => (double?)r.P90Service).Max(),
                        rows.Count > 0 ? DateTime.SpecifyKind(rows.Max(r => r.Start), DateTimeKind.Utc) : null);
                })
                .OrderBy(d => d.Desk, StringComparer.Ordinal)
                .Take(ISvcImmigrationView.MaxRows)
                .ToList();
            gateRows = gates
                .Select(g => new ImmigrationGateViewModel(g.Gate, g.Attempts, g.Rejected, Math.Round(Math.Min(1, g.BusySeconds / windowSeconds), 3),
                    g.Attempts > 0 ? Math.Round(g.BusySeconds / g.Attempts, 1) : null))
                .OrderBy(g => g.Gate, StringComparer.Ordinal)
                .Take(ISvcImmigrationView.MaxRows)
                .ToList();
        }

        return new ImmigrationHallViewModel(kind, [], lanes, eGates, desks, gateRows);
    }

    /// <summary>A per-interval mean weighted by the people processed in each interval; null when nobody was processed.</summary>
    private static double? Weighted(IReadOnlyList<DeskIntervalRow> rows, Func<DeskIntervalRow, double> value)
    {
        var served = rows.Sum(r => r.Transactions);
        return served > 0 ? Math.Round(rows.Sum(r => value(r) * r.Transactions) / served, 1) : null;
    }

    internal sealed class DeskIntervalRow
    {
        public string Kind { get; set; }
        public string Desk { get; set; }
        public string Lane { get; set; }
        public DateTime Start { get; set; }
        public int Transactions { get; set; }
        public double MeanService { get; set; }
        public double P90Service { get; set; }
        public double MeanCycle { get; set; }
    }

    internal sealed class SessionRow
    {
        public string Kind { get; set; }
        public string Desk { get; set; }
        public string State { get; set; }
        public string Lane { get; set; }
    }

    internal sealed class GateRow
    {
        public string Kind { get; set; }
        public string Gate { get; set; }
        public int Attempts { get; set; }
        public int Accepted { get; set; }
        public int Rejected { get; set; }
        public int DocumentRead { get; set; }
        public int BiometricCapture { get; set; }
        public int Eligibility { get; set; }
        public int ReferredToOfficer { get; set; }
        public int Technical { get; set; }
        public int Other { get; set; }
        public double BusySeconds { get; set; }
    }

    private sealed class QueueRow
    {
        public string Kind { get; set; }
        public string Zone { get; set; }
        public string Lane { get; set; }
    }

    private sealed class CountRow
    {
        public string Kind { get; set; }
        public int Count { get; set; }
    }

    private sealed class CodeRow
    {
        public string Code { get; set; }
    }
}
