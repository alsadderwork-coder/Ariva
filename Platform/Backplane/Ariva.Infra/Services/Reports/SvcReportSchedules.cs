using Ariva.Core;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Reports;

/// <summary>
/// Report schedules (ARV-060, <see cref="ISvcReportSchedules"/>) of the caller's sites. Recipients are Ariva accounts
/// only, never free addresses: enabled, past their first sign-in, not the break-glass account, reaching the site and
/// holding a role that reads reports. The same rule is checked again at every delivery, so a recipient who loses the site or the role stops
/// receiving. Every change is audited.
/// </summary>
internal sealed class SvcReportSchedules(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, AuditTrail audit,
    Ariva.Infra.Settings.AuthSettings auth)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcReportSchedules
{
    private const string Target = "ReportSchedule";

    public async Task<Result<IReadOnlyList<ReportScheduleViewModel>>> SearchAsync(string siteCode, CancellationToken ct = default)
    {
        var query = QueryAsNoTracking<ReportSchedule>().WithinSites(await siteScope.GetAsync(ct)).Where(s => s.DeletedOn == null);
        if (!string.IsNullOrEmpty(siteCode))
            query = query.Where(s => s.SiteCode == siteCode);
        var schedules = await query.OrderBy(s => s.SiteCode).ThenBy(s => s.Name).Take(500).ToListAsync(ct);
        var views = new List<ReportScheduleViewModel>();
        foreach (var schedule in schedules)
            views.Add(await ViewAsync(schedule, ct));
        return new Result<IReadOnlyList<ReportScheduleViewModel>>(views);
    }

    public async Task<Result<ReportScheduleViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var schedule = await VisibleAsync(id, ct);
        return schedule is null ? Result.Error<ReportScheduleViewModel>(ReportErrors.NotFound) : new Result<ReportScheduleViewModel>(await ViewAsync(schedule, ct));
    }

    public async Task<Result<IReadOnlyList<ReportRecipientViewModel>>> RecipientsAsync(string siteCode, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode))
            return Result.Error<IReadOnlyList<ReportRecipientViewModel>>(ReportErrors.NotFound);
        var rows = await EligibleAsync(siteCode, null, ct);
        return new Result<IReadOnlyList<ReportRecipientViewModel>>([.. rows.Select(r => new ReportRecipientViewModel(r.Id, r.UserName, r.DisplayName))]);
    }

    public async Task<Result<ReportScheduleViewModel>> CreateAsync(ReportScheduleRequest request, CancellationToken ct = default)
    {
        if (request is null || request.SiteCode is null || !Site.IsValidCode(request.SiteCode) || !(await siteScope.GetAsync(ct)).Allows(request.SiteCode) ||
            !await ExistsAsync(request.SiteCode, ct))
            return Result.Error<ReportScheduleViewModel>(ReportErrors.NotFound);
        var (values, recipients, problem) = await CheckAsync(request.SiteCode, request, ct);
        if (problem is not null)
            return Result.Error<ReportScheduleViewModel>(problem);
        if (await QueryAsNoTracking<ReportSchedule>().CountAsync(s => s.SiteCode == request.SiteCode && s.DeletedOn == null, ct) >= ISvcReportSchedules.MaxSchedulesPerSite)
            return Result.Error<ReportScheduleViewModel>(ReportErrors.TooMany);
        if (CurrentUser.Id is not { } owner)
            return Result.Error<ReportScheduleViewModel>(ReportErrors.NotFound);

        var schedule = new ReportSchedule(request.SiteCode, owner, values.Name, values.Template, values.SendAt, request.Enabled);
        await SaveAsync(schedule, ct);
        foreach (var id in recipients)
            await SaveAsync(new ReportScheduleRecipient(schedule, id), ct);
        // The view reads the recipients with SQL: the new rows must be in the transaction first.
        await FlushAsync(ct);
        await audit.RecordAsync($"{Target}.Created", Target, schedule.Id, schedule.Name, null, schedule.AuditSummary(recipients.Select(r => r.ToString())), ct);
        return new Result<ReportScheduleViewModel>(await ViewAsync(schedule, ct));
    }

    public async Task<Result<ReportScheduleViewModel>> UpdateAsync(Guid id, ReportScheduleRequest request, CancellationToken ct = default)
    {
        var schedule = await VisibleAsync(id, ct, track: true);
        if (schedule is null || request is null)
            return Result.Error<ReportScheduleViewModel>(ReportErrors.NotFound);
        var (values, recipients, problem) = await CheckAsync(schedule.SiteCode, request, ct);
        if (problem is not null)
            return Result.Error<ReportScheduleViewModel>(problem);

        var current = await Query<ReportScheduleRecipient>().Where(r => r.Schedule.Id == schedule.Id).ToListAsync(ct);
        var before = schedule.AuditSummary(current.Select(r => r.UserId.ToString()));
        schedule.Set(values.Name, values.Template, values.SendAt, request.Enabled);
        await UpdateAsync(schedule, ct);
        foreach (var gone in current.Where(r => !recipients.Contains(r.UserId)))
            await DeleteAsync(gone, ct);
        foreach (var added in recipients.Where(r => current.All(c => c.UserId != r)))
            await SaveAsync(new ReportScheduleRecipient(schedule, added), ct);
        await FlushAsync(ct);
        await audit.RecordAsync($"{Target}.Updated", Target, schedule.Id, schedule.Name, before, schedule.AuditSummary(recipients.Select(r => r.ToString())), ct);
        return new Result<ReportScheduleViewModel>(await ViewAsync(schedule, ct));
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var schedule = await VisibleAsync(id, ct, track: true);
        if (schedule is null)
            return Result.Error<bool>(ReportErrors.NotFound);
        var recipients = await QueryAsNoTracking<ReportScheduleRecipient>().Where(r => r.Schedule.Id == schedule.Id).Select(r => r.UserId).ToListAsync(ct);
        schedule.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(schedule, ct);
        await audit.RecordAsync($"{Target}.Deleted", Target, schedule.Id, schedule.Name, schedule.AuditSummary(recipients.Select(r => r.ToString())), null, ct);
        return new Result<bool>(true);
    }

    private async Task<((string Name, ReportTemplate Template, string SendAt) Values, List<Guid> Recipients, string Problem)> CheckAsync(
        string siteCode, ReportScheduleRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > ReportSchedule.MaxNameLength || !Ariva.Core.Domain.Components.DisplayText.IsClean(name))
            return (default, null, ReportErrors.InvalidName);
        var template = ReportTemplate.DailyPeaks;
        // A name only: Enum.TryParse would also take "0" or "DailyPeaks, DailyPeaks".
        if (request.Template is not null && (!Enum.GetNames<ReportTemplate>().Contains(request.Template, StringComparer.Ordinal) || !Enum.TryParse(request.Template, out template)))
            return (default, null, ReportErrors.InvalidTemplate);
        if (!ReportSchedule.TryParseSendAt(request.SendAt, out _))
            return (default, null, ReportErrors.InvalidSendAt);
        var ids = (request.RecipientIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > ReportSchedule.MaxRecipients)
            return (default, null, ReportErrors.InvalidRecipients);
        // Every recipient must be someone the site's report may go to; an id that is not answers the same as one that
        // does not exist, so the check reveals nothing about other accounts.
        var eligible = (await EligibleAsync(siteCode, ids, ct)).Select(r => r.Id).ToHashSet();
        if (!ids.All(eligible.Contains))
            return (default, null, ReportErrors.InvalidRecipients);
        return ((name, template, request.SendAt), ids, null);
    }

    private async Task<List<RecipientRow>> EligibleAsync(string siteCode, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object>
        {
            ["site"] = siteCode,
            ["roles"] = ReportReaders.Roles.ToList(),
            // An account still on its first sign-in (temporary password, or no authenticator where one is required) reads
            // nothing on the screen, so it is sent nothing either.
            ["totp"] = auth.TotpRequired,
            ["filter"] = ids is not null,
            // As text: a list of Guid parameters is not bound by NHibernate.
            ["ids"] = ids is { Count: > 0 } ? ids.Select(i => i.ToString("D")).ToList() : [Guid.Empty.ToString("D")]
        };
        return await ExecuteSqlAsync<RecipientRow>("""
            SELECT u.id AS "Id", u.user_name AS "UserName", u.display_name AS "DisplayName" FROM "user" u
            WHERE NOT u.is_disabled AND NOT u.is_break_glass AND NOT u.must_change_password AND (NOT :totp OR u.totp_enrolled)
              AND (u.all_sites OR EXISTS (SELECT 1 FROM user_site s WHERE s.user_id = u.id AND s.site_code = :site))
              AND EXISTS (SELECT 1 FROM user_role r WHERE r.user_id = u.id AND r.role_code IN (:roles))
              AND (NOT :filter OR CAST(u.id AS varchar(36)) IN (:ids))
            ORDER BY u.user_name LIMIT 500
            """, parameters, ct);
    }

    private async Task<bool> ExistsAsync(string siteCode, CancellationToken ct) =>
        await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == siteCode, ct);

    private async Task<ReportSchedule> VisibleAsync(Guid id, CancellationToken ct, bool track = false)
    {
        var schedule = track ? await GetAsync<ReportSchedule>(id, ct) : await QueryAsNoTracking<ReportSchedule>().Where(s => s.Id == id).FirstOrDefaultAsync(ct);
        return schedule is null || schedule.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(schedule.SiteCode) ? null : schedule;
    }

    private async Task<ReportScheduleViewModel> ViewAsync(ReportSchedule schedule, CancellationToken ct)
    {
        var recipients = await ExecuteSqlAsync<RecipientRow>("""
            SELECT u.id AS "Id", u.user_name AS "UserName", u.display_name AS "DisplayName"
            FROM report_schedule_recipient r JOIN "user" u ON u.id = r.user_id WHERE r.schedule_id = :id ORDER BY u.user_name
            """, new Dictionary<string, object> { ["id"] = schedule.Id }, ct);
        var sent = await ExecuteSqlAsync<SentRow>("""
            SELECT max(sent_utc) AS "SentUtc" FROM report_delivery WHERE schedule_id = :id AND status = 'Sent'
            """, new Dictionary<string, object> { ["id"] = schedule.Id }, ct);
        return new ReportScheduleViewModel(schedule.Id!.Value, schedule.SiteCode, schedule.Name, schedule.Template.ToString(), schedule.SendAt, schedule.Enabled,
            schedule.OwnerId, [.. recipients.Select(r => new ReportRecipientViewModel(r.Id, r.UserName, r.DisplayName))],
            sent.FirstOrDefault()?.SentUtc is { } s ? DateTime.SpecifyKind(s.ToUniversalTime(), DateTimeKind.Utc) : null);
    }

    private sealed class RecipientRow
    {
        public Guid Id { get; set; }
        public string UserName { get; set; }
        public string DisplayName { get; set; }
    }

    private sealed class SentRow
    {
        public DateTime? SentUtc { get; set; }
    }
}
