using Ariva.Core.Security;
using Ariva.Core.Services.Live;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Live;

/// <summary>
/// The live desk states of a site (ARV-055, <see cref="ISvcDeskStates"/>): each desk's latest closed minute from
/// <c>desk_minute</c> (written by Ariva.Api.Stream under the key site/checkpoint/desk), with the state that held most of the
/// minute. The query itself keeps to the desk kinds the caller may see: check-in counters and security lanes with
/// <c>AirportDesks.View</c>, immigration and emigration desks and e-gates (border data) with <c>BorderDesks.View</c>. It
/// names the allowed checkpoint kinds (fail closed), and a minute whose key ever belonged to a desk of a kind the caller
/// may not see (a deleted checkpoint included) is left out, so a reused code cannot carry border data to an airport role.
/// </summary>
internal sealed class SvcDeskStates(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, CallerRoles callerRoles)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDeskStates
{
    public async Task<Result<DeskStatesViewModel>> GetAsync(string siteCode, CancellationToken ct = default)
    {
        if (siteCode is null || !(await siteScope.GetAsync(ct)).Allows(siteCode) ||
            (await ExecuteSqlAsync<CodeRow>("""SELECT code AS "Code" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count == 0)
            return Result.Error<DeskStatesViewModel>(TopologyErrors.NotFound);

        var permissions = RolePermissions.For(await callerRoles.GetAsync(ct));
        var border = permissions.Contains(Ariva.Core.Global.Defaults.Permissions.ViewBorderDesks);
        var airport = permissions.Contains(Ariva.Core.Global.Defaults.Permissions.ViewAirportDesks);
        var now = UtcNow;
        if (!border && !airport)
            return new Result<DeskStatesViewModel>(new DeskStatesViewModel(siteCode, now, [], false, false, false));

        var rows = await ExecuteSqlAsync<DeskRow>(
            """
            SELECT DISTINCT ON (m.desk_code)
                   c.code AS "Checkpoint", c.kind AS "CheckpointKind", d.code AS "Desk", d.kind AS "DeskKind", m.lane AS "Lane",
                   m.minute_utc AS "MinuteUtc", m.closed_seconds AS "Closed", m.idle_seconds AS "Idle", m.serving_seconds AS "Serving",
                   m.paused_seconds AS "Paused", m.unknown_seconds AS "Unknown", m.transactions AS "Transactions", m.degraded AS "Degraded"
              FROM desk d
              JOIN checkpoint c ON c.id = d.checkpoint_id AND c.deleted_on IS NULL
              JOIN desk_minute m ON m.desk_code = d.site_code || '/' || c.code || '/' || d.code
             WHERE d.site_code = :site AND d.deleted_on IS NULL
               AND m.minute_utc >= :from AND m.minute_utc <= :to
               AND ((CAST(:airport AS boolean) AND c.kind IN ('CheckIn', 'Security'))
                 OR (CAST(:border AS boolean) AND c.kind IN ('Emigration', 'Immigration')))
               AND NOT EXISTS (
                   SELECT 1
                     FROM desk d2
                     JOIN checkpoint c2 ON c2.id = d2.checkpoint_id
                    WHERE d2.site_code = :site
                      AND d2.site_code || '/' || c2.code || '/' || d2.code = m.desk_code
                      AND NOT ((CAST(:airport AS boolean) AND c2.kind IN ('CheckIn', 'Security'))
                            OR (CAST(:border AS boolean) AND c2.kind IN ('Emigration', 'Immigration'))))
             ORDER BY m.desk_code, m.minute_utc DESC
             LIMIT 2001
            """,
            new Dictionary<string, object>
            {
                ["site"] = siteCode,
                ["from"] = now.AddMinutes(-ISvcDeskStates.WindowMinutes),
                ["to"] = now,
                ["border"] = border,
                ["airport"] = airport
            },
            ct);

        var desks = rows.Take(ISvcDeskStates.MaxDesks)
            .Select(r => new DeskStateViewModel(r.Checkpoint, r.CheckpointKind, r.Desk, r.DeskKind, r.Lane, DateTime.SpecifyKind(r.MinuteUtc, DateTimeKind.Utc), StateOf(r),
                r.Transactions, r.Degraded))
            .OrderBy(d => d.Checkpoint, StringComparer.Ordinal).ThenBy(d => d.Desk, StringComparer.Ordinal)
            .ToList();
        return new Result<DeskStatesViewModel>(new DeskStatesViewModel(siteCode, now, desks, airport, border, rows.Count > ISvcDeskStates.MaxDesks));
    }

    /// <summary>The state that held most of the minute; a tie goes to the more active state (serving, idle, paused, closed, unknown).</summary>
    internal static string StateOf(DeskRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        (string State, double Seconds)[] states = [("Serving", row.Serving), ("Idle", row.Idle), ("Paused", row.Paused), ("Closed", row.Closed), ("Unknown", row.Unknown)];
        var best = states[0];
        foreach (var candidate in states)
        {
            if (candidate.Seconds > best.Seconds)
                best = candidate;
        }

        return best.Seconds > 0 ? best.State : "Unknown";
    }

    internal sealed class DeskRow
    {
        public string Checkpoint { get; set; }
        public string CheckpointKind { get; set; }
        public string Desk { get; set; }
        public string DeskKind { get; set; }
        public string Lane { get; set; }
        public DateTime MinuteUtc { get; set; }
        public double Closed { get; set; }
        public double Idle { get; set; }
        public double Serving { get; set; }
        public double Paused { get; set; }
        public double Unknown { get; set; }
        public int Transactions { get; set; }
        public bool Degraded { get; set; }
    }

    private sealed class CodeRow
    {
        public string Code { get; set; }
    }
}
