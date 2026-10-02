using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Live;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using System.Text.Json;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Alerting;

/// <summary>
/// Alerts (ARV-039). An alert is visible, and can be acted on, only within the caller's sites and where the caller's
/// role is responsible for it (<see cref="Alert.IsFor"/>); any other alert answers like one that does not exist
/// (CWE-863). An action locks the alert's row first, so it never overwrites what the evaluation or another person did
/// in between, moves the alert forward only (otherwise 409), is audited, and is announced to the live hub after commit.
/// </summary>
internal sealed class SvcAlerts(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    CallerRoles callerRoles,
    AuditTrail audit,
    IAlertNotices notices,
    Ariva.Infra.Notifications.AlertEmails emails) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAlerts
{
    private const string Target = "Alert";

    public async Task<Result<PageViewModel<AlertViewModel>>> SearchAsync(AlertCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new AlertCriteria();
        AlertState? state = null;
        if (criteria.State is not null)
        {
            if (!Enumerable.Contains(Enum.GetNames<AlertState>(), criteria.State, StringComparer.Ordinal))
                return Result.Error<PageViewModel<AlertViewModel>>(TopologyErrors.InvalidCriteria);
            state = Enum.Parse<AlertState>(criteria.State);
        }

        if ((criteria.FromUtc is { Kind: not DateTimeKind.Utc }) || (criteria.ToUtc is { Kind: not DateTimeKind.Utc }))
            return Result.Error<PageViewModel<AlertViewModel>>(TopologyErrors.InvalidCriteria);

        var roles = (await callerRoles.GetAsync(ct)).ToList();
        var query = Visible(QueryAsNoTracking<Alert>().WithinSites(await siteScope.GetAsync(ct)), roles);
        if (!string.IsNullOrWhiteSpace(criteria.SiteCode))
            query = query.Where(a => a.SiteCode == criteria.SiteCode);
        if (state is { } s)
            query = query.Where(a => a.State == s);
        if (criteria.Open is { } open)
            query = open ? query.Where(a => a.State != AlertState.Resolved) : query.Where(a => a.State == AlertState.Resolved);
        if (!string.IsNullOrWhiteSpace(criteria.ZoneName))
            query = query.Where(a => a.ZoneName == criteria.ZoneName);
        if (!string.IsNullOrWhiteSpace(criteria.RuleCode))
            query = query.Where(a => a.RuleCode == criteria.RuleCode);
        if (criteria.FromUtc is { } from)
            query = query.Where(a => a.RaisedUtc >= from);
        if (criteria.ToUtc is { } to)
            query = query.Where(a => a.RaisedUtc <= to);

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.RaisedUtc).ThenBy(a => a.Id).Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<AlertViewModel>>(new PageViewModel<AlertViewModel>(rows.Select(View).ToList(), total, pageIndex, pageSize));
    }

    public async Task<Result<AlertViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var alert = await VisibleAsync(id, lockRow: false, ct);
        return alert is null ? Result.Error<AlertViewModel>(TopologyErrors.NotFound) : new Result<AlertViewModel>(View(alert));
    }

    public Task<Result<AlertViewModel>> AcknowledgeAsync(Guid id, AlertActionRequest request, CancellationToken ct = default) =>
        ActAsync(id, request, required: false, "Acknowledged", (a, by, now, note) => a.Acknowledge(by, now, note), ct);

    public Task<Result<AlertViewModel>> EscalateAsync(Guid id, AlertActionRequest request, CancellationToken ct = default) =>
        ActAsync(id, request, required: false, "Escalated", (a, by, now, note) => a.Escalate(by, now, note), ct);

    public Task<Result<AlertViewModel>> ResolveAsync(Guid id, AlertActionRequest request, CancellationToken ct = default) =>
        ActAsync(id, request, required: true, "Resolved", (a, by, now, note) => a.ResolveManually(by, now, note), ct);

    private async Task<Result<AlertViewModel>> ActAsync(Guid id, AlertActionRequest request, bool required, string action, Func<Alert, string, DateTime, string, bool> act,
        CancellationToken ct)
    {
        var note = request?.Note;
        var alert = await VisibleAsync(id, lockRow: true, ct);
        if (alert is null)
            return Result.Error<AlertViewModel>(TopologyErrors.NotFound);
        if (!Alert.IsValidNote(note, required))
            return Result.Error<AlertViewModel>(AlertErrors.InvalidNote);
        var before = alert.State;
        if (!act(alert, CurrentUser.UserName, UtcNow, note))
            return Result.Error<AlertViewModel>(AlertErrors.InvalidTransition);

        await UpdateAsync(alert, ct);
        // JSON, so a note cannot pass for another field of the summary (CWE-117).
        await audit.RecordAsync($"{Target}.{action}", Target, alert.Id, Name(alert), JsonSerializer.Serialize(new { state = before.ToString() }),
            JsonSerializer.Serialize(new { state = alert.State.ToString(), note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }), ct);
        if (alert.State == AlertState.Escalated && before != AlertState.Escalated)
            await emails.EnqueueAsync(alert, EmailKind.AlertEscalated, UtcNow, ct);
        var notice = AlertNotice.From(alert, UtcNow);
        UnitOfWork.RegisterPostCommitAction(() => notices.PublishAsync([notice], CancellationToken.None));
        return new Result<AlertViewModel>(View(alert));
    }

    // The alert, if it is in the caller's sites and the caller's role is responsible; for an action it is checked first
    // without loading it (so no one can hold the lock of an alert they cannot see), then locked, so the evaluation and
    // other people wait for this transaction and see what it wrote, then loaded and checked again.
    private async Task<Alert> VisibleAsync(Guid id, bool lockRow, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return null;
        if (lockRow)
        {
            var roles = (await callerRoles.GetAsync(ct)).ToList();
            // A count, not Any: NHibernate's Any can load the entity into the session, and the load after the lock would
            // then return that copy from before the lock instead of what another transaction committed meanwhile.
            var visible = await Visible(QueryAsNoTracking<Alert>().WithinSites(await siteScope.GetAsync(ct)), roles).Where(a => a.Id == id).CountAsync(ct);
            if (visible == 0)
                return null;
            await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM alert WHERE id = :id FOR UPDATE""", new Dictionary<string, object> { ["id"] = id }, ct);
        }

        var alert = await GetAsync<Alert>(id, ct);
        if (alert is null || !(await siteScope.GetAsync(ct)).Allows(alert.SiteCode) || !alert.IsFor(await callerRoles.GetAsync(ct)))
            return null;
        return alert;
    }

    private static IQueryable<Alert> Visible(IQueryable<Alert> query, IReadOnlyCollection<string> roles)
    {
        if (Enumerable.Contains(roles, Ariva.Core.RoleCodes.SystemAdministrator, StringComparer.Ordinal))
            return query;
        var held = roles.ToList();
        return query.Where(a => a.OwnerRole == null || held.Contains(a.OwnerRole) || (a.EscalatedUtc != null && held.Contains(a.EscalateToRole)));
    }

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    private static string Name(Alert a) => $"{a.SiteCode}:{a.RuleCode}:{a.ZoneName}{(a.DeviceCode is null ? "" : ":" + a.DeviceCode)}";

    internal static AlertViewModel View(Alert a) =>
        new(a.Id!.Value, a.SiteCode, a.RuleId, a.RuleCode, a.RuleName, a.ZoneName, a.DeviceCode, a.Metric.ToString(), a.Severity.ToString(), a.State.ToString(),
            a.RaisedUtc, a.RaisedValue, a.BinStartUtc, a.PredictedForUtc, a.OwnerRole, a.EscalateAfterMinutes, a.EscalateToRole, a.EscalationContact,
            a.EscalateAfterMinutes is { } m && a.EscalatedUtc is null && a.State == AlertState.Raised ? a.RaisedUtc.AddMinutes(m) : null,
            a.AcknowledgedUtc, a.AcknowledgedBy, a.AcknowledgedNote, a.EscalatedUtc, a.EscalatedBy, a.EscalatedNote, a.ClearedUtc, a.ResolvedUtc,
            a.Resolution?.ToString(), a.ResolvedBy, a.ResolutionNote);
}
