using System.Data;
using System.Globalization;
using System.Text;
using Ariva.Core;
using Ariva.Core.Reports;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Notifications;
using Ariva.Infra.Services.Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ariva.Infra.Services.Reports;

/// <summary>
/// The scheduled daily reports (ARV-060, <see cref="ISvcReportDeliveries"/>), run by TickerQ in Ariva.Api.Cronz through
/// <see cref="ReportDeliveryRound"/>. Preparing a round, under a transaction advisory lock: every enabled schedule whose
/// local send time has passed today owes the previous local day to each recipient, one delivery row per schedule, day
/// and recipient (unique), so the round is idempotent. Each delivery then runs in its own transaction, claimed with
/// FOR UPDATE SKIP LOCKED: the recipient is checked again (enabled, able to sign in, the site, a role that reads reports,
/// a usable address) and gets the report its own roles see, with the hours, alerts and devices as CSV attachments. A
/// report that cannot be built or an email the relay refuses marks that delivery Failed, retried by later rounds up to the
/// attempt limit; the others go on. The send comes just before its own commit, so only a crash between the two can send a
/// report twice (at least once, never lost).
/// </summary>
internal sealed class SvcReportDeliveries(IUnitOfWork unitOfWork, TimeProvider timeProvider, ReportReader reader, IEmailTransport transport,
    EmailSettings settings, Ariva.Infra.Settings.AuthSettings auth, ILogger<SvcReportDeliveries> logger) : SvcDb(unitOfWork), ISvcReportDeliveries
{
    /// <summary>The transaction advisory lock key of a delivery round (any constant no other lock uses).</summary>
    public const long RoundLock = 0x4152_5641_0060;

    /// <summary>At most this many deliveries are sent in one round; the next round takes the rest.</summary>
    public const int MaxPerRound = 200;

    public async Task<IReadOnlyList<Guid>> PrepareAsync(CancellationToken ct = default)
    {
        // Email off (vm-local without a relay): nothing is owed, so nothing piles up to be sent later at once.
        if (!settings.Enabled)
            return [];
        Begin();
        var locked = await ExecuteSqlAsync<LockRow>("""SELECT pg_try_advisory_xact_lock(:key) AS "Taken" """, new Dictionary<string, object> { ["key"] = RoundLock }, ct);
        if (!locked.Single().Taken)
            return [];
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var schedules = await ExecuteSqlAsync<ScheduleRow>("""
            SELECT id AS "Id", site_code AS "SiteCode", name AS "Name", send_at AS "SendAt" FROM report_schedule
            WHERE enabled AND deleted_on IS NULL ORDER BY site_code, name
            """, null, ct);
        foreach (var schedule in schedules)
        {
            var tz = await reader.TimeZoneAsync(schedule.SiteCode, ct);
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, tz);
            if (!Ariva.Core.Domain.Entities.ReportSchedule.TryParseSendAt(schedule.SendAt, out var at) || TimeOnly.FromDateTime(local) < at)
                continue;
            var date = DateOnly.FromDateTime(local).AddDays(-1);
            // One row per recipient of the schedule, once: later rounds find it and do not create it again.
            await ExecuteCommandAsync<IdRow>("""
                INSERT INTO report_delivery (id, schedule_id, report_date, recipient_id, status, attempts, created_utc)
                SELECT gen_random_uuid(), r.schedule_id, :date, r.user_id, 'Pending', 0, :now FROM report_schedule_recipient r
                WHERE r.schedule_id = :schedule
                ON CONFLICT (schedule_id, report_date, recipient_id) DO NOTHING
                RETURNING id AS "Id"
                """, new Dictionary<string, object> { ["date"] = date.ToDateTime(TimeOnly.MinValue), ["now"] = now, ["schedule"] = schedule.Id }, ct);
        }

        var due = await ExecuteSqlAsync<IdRow>("""
            SELECT d.id AS "Id" FROM report_delivery d JOIN report_schedule rs ON rs.id = d.schedule_id
            WHERE rs.enabled AND rs.deleted_on IS NULL
              AND (d.status = 'Pending' OR (d.status = 'Failed' AND d.attempts < :max))
              AND d.report_date >= :oldest
            ORDER BY d.created_utc, d.id LIMIT :limit
            """, new Dictionary<string, object>
        {
            ["max"] = ISvcReportDeliveries.MaxAttempts,
            // A report owed for more than two days is not sent late: the next day's report is the useful one.
            ["oldest"] = DateOnly.FromDateTime(now).AddDays(-3).ToDateTime(TimeOnly.MinValue),
            ["limit"] = MaxPerRound
        }, ct);
        UnitOfWork.PromiseToCommit();
        return [.. due.Select(d => d.Id)];
    }

    public async Task<ReportDeliveryOutcome> DeliverAsync(Guid deliveryId, CancellationToken ct = default)
    {
        Begin();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Claimed with whether the recipient may still have the report: the same rule as choosing it, checked now.
        var rows = await ExecuteSqlAsync<DueRow>("""
            SELECT d.id AS "Id", d.schedule_id AS "ScheduleId", CAST(d.report_date AS timestamp) AS "ReportDate", d.attempts AS "Attempts",
                   rs.site_code AS "SiteCode", rs.name AS "ScheduleName", u.id AS "UserId", u.email AS "Email",
                   (NOT u.is_disabled AND NOT u.is_break_glass AND NOT u.must_change_password AND (NOT :totp OR u.totp_enrolled)
                    AND (u.all_sites OR EXISTS (SELECT 1 FROM user_site s WHERE s.user_id = u.id AND s.site_code = rs.site_code))
                    AND EXISTS (SELECT 1 FROM user_role r WHERE r.user_id = u.id AND r.role_code IN (:roles))) AS "Eligible"
            FROM report_delivery d JOIN report_schedule rs ON rs.id = d.schedule_id JOIN "user" u ON u.id = d.recipient_id
            WHERE d.id = :id AND rs.enabled AND rs.deleted_on IS NULL
              AND (d.status = 'Pending' OR (d.status = 'Failed' AND d.attempts < :max))
            FOR UPDATE OF d SKIP LOCKED
            """, new Dictionary<string, object>
        {
            ["id"] = deliveryId,
            ["roles"] = ReportReaders.Roles.ToList(),
            ["totp"] = auth.TotpRequired,
            ["max"] = ISvcReportDeliveries.MaxAttempts
        }, ct);
        if (rows.Count == 0)
            return ReportDeliveryOutcome.None;
        var row = rows[0];

        if (!row.Eligible || string.IsNullOrWhiteSpace(row.Email) || !EmailAddresses.IsValid(row.Email))
        {
            await MarkAsync(row.Id, "Skipped", row.Attempts, row.Eligible ? "The account has no usable email address." : "The account may no longer read this site's reports.", null, ct);
            UnitOfWork.PromiseToCommit();
            return ReportDeliveryOutcome.Skipped;
        }

        OutgoingEmail email;
        try
        {
            var report = await reader.ReadAsync(row.SiteCode, DateOnly.FromDateTime(row.ReportDate), await RolesAsync(row.UserId, ct), now, ct);
            email = Email(row.Id, row.Email.Trim(), new ScheduleRow { Id = row.ScheduleId, SiteCode = row.SiteCode, Name = row.ScheduleName }, report);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // This delivery only: it is retried by later rounds up to the limit, and the round goes on with the others.
            logger.LogError(e, "Report delivery {DeliveryId} of schedule {ScheduleId}: the report could not be built", row.Id, row.ScheduleId);
            await MarkAsync(row.Id, "Failed", row.Attempts + 1, "The report could not be built.", null, ct);
            UnitOfWork.PromiseToCommit();
            return ReportDeliveryOutcome.Failed;
        }

        var failures = await transport.SendAsync([email], ct);
        if (failures.TryGetValue(row.Id, out var reason))
        {
            await MarkAsync(row.Id, "Failed", row.Attempts + 1, EmailTemplates.Clean(reason), null, ct);
            logger.LogWarning("Report delivery {DeliveryId} of schedule {ScheduleId} failed (attempt {Attempt})", row.Id, row.ScheduleId, row.Attempts + 1);
            UnitOfWork.PromiseToCommit();
            return ReportDeliveryOutcome.Failed;
        }

        await MarkAsync(row.Id, "Sent", row.Attempts + 1, null, now, ct);
        UnitOfWork.PromiseToCommit();
        return ReportDeliveryOutcome.Sent;
    }

    private void Begin()
    {
        if (!UnitOfWork.StorageProvider.IsTransactionActive())
            UnitOfWork.StorageProvider.BeginTransaction(IsolationLevel.ReadCommitted);
    }

    private async Task<IReadOnlyList<string>> RolesAsync(Guid userId, CancellationToken ct) =>
        [.. (await ExecuteSqlAsync<TextRow>("""SELECT role_code AS "Value" FROM user_role WHERE user_id = :id ORDER BY role_code""",
            new Dictionary<string, object> { ["id"] = userId }, ct)).Select(r => r.Value)];

    // Typed parameters only (an untyped null cannot be bound), so a sent delivery and the others take separate statements.
    private Task<List<IdRow>> MarkAsync(Guid id, string status, int attempts, string reason, DateTime? sentUtc, CancellationToken ct) =>
        sentUtc is { } sent
            ? ExecuteCommandAsync<IdRow>("""
                UPDATE report_delivery SET status = :status, attempts = :attempts, reason = NULL, sent_utc = :sent WHERE id = :id RETURNING id AS "Id"
                """, new Dictionary<string, object> { ["id"] = id, ["status"] = status, ["attempts"] = attempts, ["sent"] = sent }, ct)
            : ExecuteCommandAsync<IdRow>("""
                UPDATE report_delivery SET status = :status, attempts = :attempts, reason = NULLIF(:reason, '') WHERE id = :id RETURNING id AS "Id"
                """, new Dictionary<string, object> { ["id"] = id, ["status"] = status, ["attempts"] = attempts, ["reason"] = reason ?? string.Empty }, ct);

    /// <summary>The email: a plain-text summary and the three CSV files. Names typed by people are cleaned of control characters.</summary>
    internal static OutgoingEmail Email(Guid id, string recipient, ScheduleRow schedule, DailyReport report)
    {
        var date = report.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Daily report for {report.SiteCode}, {date} ({report.TimeZoneId}).\n")
            .Append(CultureInfo.InvariantCulture, $"Schedule: {EmailTemplates.Clean(schedule.Name, 120)}\n\n");
        var h = report.Headline;
        body.Append(h.WorstPeakP90Minutes is { } worst
            ? string.Create(CultureInfo.InvariantCulture, $"Worst peak hour: {EmailTemplates.Clean(h.WorstPeakLane)} at {h.WorstPeakHour}, P90 {worst:0.#} min.\n")
            : "No peak hour: no lane had enough waits in one hour.\n");
        body.Append(CultureInfo.InvariantCulture, $"Zone-hours above {DailyReports.TargetMinutes:0} min at P90: {h.ZoneHoursAboveTarget}.\n");
        body.Append(CultureInfo.InvariantCulture, $"Alerts: {h.Alerts} ({h.CriticalAlerts} critical).\n");
        body.Append(h.LowestUptimePercent is { } low ? string.Create(CultureInfo.InvariantCulture, $"Lowest device uptime: {low:0.##} %.\n") : "No devices.\n");
        body.Append("\nPeaks by lane:\n");
        foreach (var lane in report.Lanes)
        {
            body.Append(lane.Peak is { } p
                ? string.Create(CultureInfo.InvariantCulture, $"  {EmailTemplates.Clean(lane.Zone)}: {p.Start} to {p.End}, P90 {p.P90Minutes:0.#} min, {p.Waits} waits\n")
                : string.Create(CultureInfo.InvariantCulture, $"  {EmailTemplates.Clean(lane.Zone)}: no peak hour\n"));
        }

        if (report.Lanes.Any(l => l.Provisional))
            body.Append("\nSome hours are provisional and may still change.\n");
        body.Append("\nThe attached CSV files hold every hour, alert and device. Times are the site's local time.\n");

        var stem = $"ariva-{report.SiteCode}-{date}";
        return new OutgoingEmail(id, recipient, $"Ariva daily report {report.SiteCode} {date}", body.ToString())
        {
            Attachments =
            [
                new EmailAttachment($"{stem}-hours.csv", "text/csv", Utf8(ReportCsv.Write(report, ReportCsv.Section.Hours))),
                new EmailAttachment($"{stem}-alerts.csv", "text/csv", Utf8(ReportCsv.Write(report, ReportCsv.Section.Alerts))),
                new EmailAttachment($"{stem}-devices.csv", "text/csv", Utf8(ReportCsv.Write(report, ReportCsv.Section.Devices)))
            ]
        };
    }

    // With a byte order mark, so spreadsheet programs read Arabic names as UTF-8.
    private static byte[] Utf8(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    internal sealed class ScheduleRow
    {
        public Guid Id { get; set; }
        public string SiteCode { get; set; }
        public string Name { get; set; }
        public string SendAt { get; set; }
    }

    private sealed class DueRow
    {
        public Guid Id { get; set; }
        public Guid ScheduleId { get; set; }
        public DateTime ReportDate { get; set; }
        public int Attempts { get; set; }
        public string SiteCode { get; set; }
        public string ScheduleName { get; set; }
        public Guid UserId { get; set; }
        public string Email { get; set; }
        public bool Eligible { get; set; }
    }

    private sealed class LockRow
    {
        public bool Taken { get; set; }
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    private sealed class TextRow
    {
        public string Value { get; set; }
    }
}

/// <summary>
/// One delivery round (ARV-060): the due deliveries are prepared in one short transaction, then each runs in a scope and
/// transaction of its own, so a failure costs that delivery alone and a sent report is committed as sent at once.
/// </summary>
public sealed class ReportDeliveryRound(IServiceScopeFactory scopes, ILogger<ReportDeliveryRound> logger)
{
    public async Task<ReportDeliveryRun> RunAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            try
            {
                due = await scope.ServiceProvider.GetRequiredService<ISvcReportDeliveries>().PrepareAsync(ct);
                await unitOfWork.EndAsync(ct);
            }
            catch
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        int sent = 0, skipped = 0, failed = 0;
        foreach (var id in due)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            try
            {
                var outcome = await scope.ServiceProvider.GetRequiredService<ISvcReportDeliveries>().DeliverAsync(id, ct);
                await unitOfWork.EndAsync(ct);
                sent += outcome == ReportDeliveryOutcome.Sent ? 1 : 0;
                skipped += outcome == ReportDeliveryOutcome.Skipped ? 1 : 0;
                failed += outcome == ReportDeliveryOutcome.Failed ? 1 : 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Left as it was (Pending or Failed): a later round tries again; this round goes on.
                await unitOfWork.RollbackAsync(CancellationToken.None);
                logger.LogError(e, "Report delivery {DeliveryId} failed and is left for a later round", id);
                failed++;
            }
        }

        return new ReportDeliveryRun(due.Count, sent, skipped, failed);
    }
}
