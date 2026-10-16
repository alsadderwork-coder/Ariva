using System.Data;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Infra.Live;
using Ariva.Infra.Services.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NHibernate.Linq;

namespace Ariva.Infra.Alerting;

/// <summary>Settings of the live alert evaluation (<c>Alerts:Evaluation</c>).</summary>
public sealed record AlertEvaluationSettings
{
    public const string SectionName = "Alerts:Evaluation";

    /// <summary>Runs the evaluation worker in this host (Ariva.Api.Stream).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often the worker evaluates the rules.</summary>
    public int IntervalSeconds { get; init; } = 60;

    /// <summary>The most minutes one tick takes per target: after a longer stop the evaluation continues from the last ones.</summary>
    public int MaxCatchUpMinutes { get; init; } = 180;

    public IEnumerable<string> Problems()
    {
        if (IntervalSeconds is < 5 or > 600)
            yield return "Alerts:Evaluation:IntervalSeconds is 5 to 600.";
        if (MaxCatchUpMinutes is < 1 or > 1440)
            yield return "Alerts:Evaluation:MaxCatchUpMinutes is 1 to 1440.";
    }
}

/// <summary>What one tick did; <see cref="Failed"/> counts steps whose work failed (each logged).</summary>
public sealed record AlertTickResult(bool Ran, int Rules, int Targets, int Raised, int Cleared, int Withdrawn, int Failed = 0, int Escalated = 0);

/// <summary>What one rule's evaluation did in a tick.</summary>
public sealed record AlertRuleTickResult(int Targets, int Raised, int Cleared, int Withdrawn);

/// <summary>What a tick did besides the rules: alerts withdrawn with their rules, and alerts escalated.</summary>
public sealed record AlertHousekeeping(int Withdrawn, int Escalated);

/// <summary>
/// The live evaluation of alert rules (ARV-038), one tick a minute. A tick holds an advisory lock in a transaction of
/// its own for its whole run, so one replica evaluates and the others skip; it then withdraws the alerts of disabled
/// and deleted rules, escalates the alerts that stayed unacknowledged for their rule's escalation minutes (ARV-039),
/// and evaluates each enabled rule in its own scope and transaction (<see cref="AlertRuleTick"/>), so a rule that fails
/// is logged and retried at the next tick without holding up the others.
/// </summary>
public sealed class AlertEvaluation(IServiceScopeFactory scopes, AlertEvaluationSettings settings, ILogger<AlertEvaluation> logger)
{
    public const string SystemUserName = Alert.AutomaticResolver;

    public async Task<AlertTickResult> TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        var now = AlertInputs.Utc(nowUtc);
        await using var lockScope = scopes.CreateAsyncScope();
        var lockWork = lockScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var storage = lockWork.StorageProvider;
        try
        {
            if (!storage.IsTransactionActive())
                storage.BeginTransaction(IsolationLevel.ReadCommitted);
            var acquired = (await storage.ExecuteSqlAsync<LockRow>("""SELECT pg_try_advisory_xact_lock(38, 0) AS "Acquired" """, null, ct)).Single().Acquired;
            if (!acquired)
                return new AlertTickResult(false, 0, 0, 0, 0, 0);
            var ruleIds = await storage.Query<AlertRule>().Where(r => r.Enabled).OrderBy(r => r.SiteCode).ThenBy(r => r.Code).Select(r => r.Id!.Value).ToListAsync(ct);

            int targets = 0, raised = 0, cleared = 0, failed = 0, withdrawn = 0, escalated = 0;
            try
            {
                var housekeeping = await InScopeAsync(tick => tick.HousekeepAsync(now, ct));
                (withdrawn, escalated) = (housekeeping.Withdrawn, housekeeping.Escalated);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // withdrawing is retried next tick; the rules are still evaluated
            catch (Exception e)
#pragma warning restore CA1031
            {
                failed++;
                logger.LogError(e, "Withdrawing or escalating alerts failed; it is tried again at the next tick");
            }

            foreach (var id in ruleIds)
            {
                try
                {
                    var result = await InScopeAsync(tick => tick.EvaluateAsync(id, now, settings.MaxCatchUpMinutes, ct));
                    (targets, raised, cleared, withdrawn) = (targets + result.Targets, raised + result.Raised, cleared + result.Cleared, withdrawn + result.Withdrawn);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // one rule's failure is logged and retried next tick; the others go on
                catch (Exception e)
#pragma warning restore CA1031
                {
                    failed++;
                    logger.LogError(e, "Alert evaluation of rule {RuleId} failed; it is tried again at the next tick", id);
                }
            }

            if (raised + cleared + withdrawn + escalated + failed > 0)
                logger.LogInformation(
                    "Alert evaluation: {Raised} raised, {Cleared} cleared, {Withdrawn} withdrawn, {Escalated} escalated, {Failed} failed over {Targets} targets of {Rules} rules",
                    raised, cleared, withdrawn, escalated, failed, targets, ruleIds.Count);
            return new AlertTickResult(true, ruleIds.Count, targets, raised, cleared, withdrawn, failed, escalated);
        }
        finally
        {
            // Ending the lock's transaction releases the lock; it wrote nothing.
            await lockWork.RollbackAsync(CancellationToken.None);
        }
    }

    private async Task<T> InScopeAsync<T>(Func<AlertRuleTick, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            var result = await work(scope.ServiceProvider.GetRequiredService<AlertRuleTick>());
            await unitOfWork.EndAsync(CancellationToken.None);
            return result;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private sealed class LockRow
    {
        public bool Acquired { get; set; }
    }
}

/// <summary>
/// One rule's evaluation in a tick (ARV-038), in one unit of work: its targets' minutes since their last tick are folded
/// (<see cref="AlertEvaluator"/> on <see cref="AlertInputs"/>), raises become alerts and clears resolve them, and each
/// target's state is kept for the next tick. A zone's minutes are taken up to the last one the stream stored and that has
/// ended, a device's up to the last minute that has ended, at most the catch-up per tick. A new target (a new or
/// re-enabled rule, a zone added, a device commissioned) starts at the present: history is for the backtest. An edit of
/// the rule restarts its counts (an open alert stays and clears under the new values, unless the metric changed, which
/// resolves it); a target that left the rule has its open alert resolved and its state dropped.
/// </summary>
internal sealed class AlertRuleTick(IUnitOfWork unitOfWork, ICurrentUser currentUser, AlertInputs inputs, AuditTrail audit, IAlertNotices notices,
    Ariva.Infra.Notifications.AlertEmails emails, ILogger<AlertRuleTick> logger)
{
    /// <summary>The most alerts one tick escalates (the rest at the next).</summary>
    public const int MaxEscalationsPerTick = 500;

    private readonly List<Alert> _changed = [];

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    // An alert locked for this transaction before it is changed, so the evaluation never overwrites what someone did
    // through the API in between (and they wait for it).
    private async Task<Alert> LockedAsync(Guid id, CancellationToken ct)
    {
        await Storage.ExecuteSqlAsync<LockRow>("""SELECT 1 AS "Value" FROM alert WHERE id = :id FOR UPDATE""", new Dictionary<string, object> { ["id"] = id }, ct);
        return await Storage.GetAsync<Alert>(id, ct);
    }

    private void Changed(Alert alert) => _changed.Add(alert);

    // Announces the changed alerts once the transaction has committed.
    private void Announce(DateTime now)
    {
        if (_changed.Count == 0)
            return;
        var batch = _changed.Distinct().ToList();
        _changed.Clear();
        unitOfWork.RegisterPostCommitAction(() => notices.PublishAsync([.. batch.Select(a => AlertNotice.From(a, now))], CancellationToken.None));
    }

    private void Begin()
    {
        currentUser.SetSystemUser(Guid.Empty, AlertEvaluation.SystemUserName);
        if (!Storage.IsTransactionActive())
            Storage.BeginTransaction(IsolationLevel.ReadCommitted);
    }

    /// <summary>
    /// Resolves the open alerts of disabled and deleted rules and drops those rules' states (the session hides deleted
    /// rules, so both are found with SQL), and escalates the alerts still Raised past their rule's escalation minutes to
    /// its escalation role or contact (ARV-039), each audited.
    /// </summary>
    public async Task<AlertHousekeeping> HousekeepAsync(DateTime now, CancellationToken ct)
    {
        Begin();
        var stale = await Storage.ExecuteSqlAsync<IdRow>("""
            SELECT s.id AS "Id" FROM alert_rule_state s JOIN alert_rule r ON r.id = s.rule_id WHERE r.deleted_on IS NOT NULL OR NOT r.enabled
            """, null, ct);
        foreach (var row in stale)
            if (await Storage.GetAsync<AlertRuleState>(row.Id, ct) is { } state)
                await Storage.DeleteAsync(state, ct);
        await Storage.FlushAsync(ct);

        var open = await Storage.ExecuteSqlAsync<IdRow>("""
            SELECT a.id AS "Id" FROM alert a JOIN alert_rule r ON r.id = a.rule_id WHERE a.state <> 'Resolved' AND (r.deleted_on IS NOT NULL OR NOT r.enabled)
            """, null, ct);
        foreach (var row in open)
            if (await LockedAsync(row.Id, ct) is { IsOpen: true } alert)
            {
                alert.Resolve(AlertResolution.RuleWithdrawn, now, AlertEvaluation.SystemUserName);
                Changed(alert);
            }

        var due = await Storage.ExecuteSqlAsync<IdRow>("""
            SELECT id AS "Id" FROM alert
            WHERE state = 'Raised' AND escalated_utc IS NULL AND escalate_after_minutes IS NOT NULL AND raised_utc + escalate_after_minutes * interval '1 minute' <= :now
            ORDER BY raised_utc LIMIT :limit
            """, new Dictionary<string, object> { ["now"] = now, ["limit"] = MaxEscalationsPerTick }, ct);
        var escalated = 0;
        foreach (var row in due)
        {
            if (await LockedAsync(row.Id, ct) is not { } alert || !alert.IsDueForEscalation(now) ||
                !alert.Escalate(AlertEvaluation.SystemUserName, now, null))
                continue;
            // JSON, so the free-text contact cannot pass for another field (CWE-117).
            await audit.RecordAsync("Alert.Escalated", "Alert", alert.Id, $"{alert.SiteCode}:{alert.RuleCode}:{alert.ZoneName}",
                System.Text.Json.JsonSerializer.Serialize(new { state = "Raised" }),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    state = "Escalated", to = alert.EscalateToRole, contact = alert.EscalationContact, afterMinutes = alert.EscalateAfterMinutes, by = "evaluation"
                }), ct);
            Changed(alert);
            await emails.EnqueueAsync(alert, EmailKind.AlertEscalated, now, ct);
            escalated++;
        }

        await Storage.FlushAsync(ct);
        Announce(now);
        unitOfWork.PromiseToCommit();
        return new AlertHousekeeping(open.Count, escalated);
    }

    public async Task<AlertRuleTickResult> EvaluateAsync(Guid ruleId, DateTime now, int maxCatchUpMinutes, CancellationToken ct)
    {
        Begin();
        var rule = await Storage.GetAsync<AlertRule>(ruleId, ct);
        if (rule is null || rule.IsDeleted || !rule.Enabled)
            return new AlertRuleTickResult(0, 0, 0, 0);
        var values = rule.Values();
        // ARV-181b2 (CWE-863): a stored rule on a border-only metric that fails the rule checks (no owner or an airport-side
        // owner or escalation role, a comparator below: one from before ARV-181b2 or changed in the database) is never
        // judged, so it raises nothing for anyone; every output channel filters such alerts by metric besides.
        if (BorderAlerts.IsBorderOnly(values.Metric) && values.Problems().Count > 0)
            return NotEvaluated(rule, now, "it fails the rule checks of its metric (border roles only, fires above)");
        var version = rule.ValuesHash();
        // The last minute that has ended.
        var ended = AlertInputs.Minute(now).AddMinutes(-1);
        var targets = await inputs.TargetsAsync(rule.SiteCode, values, ct);
        var current = targets.ToDictionary(t => (t.ZoneName, t.DeviceCode ?? string.Empty));
        var states = await Storage.Query<AlertRuleState>().Where(s => s.RuleId == ruleId).ToListAsync(ct);
        int withdrawn = 0, raised = 0, cleared = 0;

        // Targets that left the rule: their open alerts are resolved and their states dropped.
        foreach (var gone in states.Where(s => !current.ContainsKey((s.ZoneName, s.DeviceCode))).ToList())
        {
            if (gone.OpenAlertId is { } id && await LockedAsync(id, ct) is { IsOpen: true } alert)
            {
                alert.Resolve(AlertResolution.TargetWithdrawn, now, AlertEvaluation.SystemUserName);
                Changed(alert);
                withdrawn++;
            }

            await Storage.DeleteAsync(gone, ct);
            states.Remove(gone);
        }

        var byTarget = states.ToDictionary(s => (s.ZoneName, s.DeviceCode));
        var zoneNames = targets.Select(t => t.ZoneName).Distinct(StringComparer.Ordinal).ToList();
        var latest = values.Metric switch
        {
            AlertMetric.SensorOffline => null,
            // ARV-115: overflow rules run on the bands' own minutes, which an overflow zone has without a queue minute of its own.
            AlertMetric.OverflowOccupied => await inputs.LatestOverflowMinutesAsync(rule.SiteCode, zoneNames, ended, ct),
            // ARV-114b: a health-check rule waits for each zone's latest ended bin to settle, so it judges what a backtest would.
            AlertMetric.ConservationResidual or AlertMetric.TrackCompletion or AlertMetric.OccupancyOutOfRange =>
                await inputs.LatestHealthMinutesAsync(rule.SiteCode, values.Metric, zoneNames, ended, ct),
            // ARV-181b2: open desks below recommendation runs on the minutes the staffing job computed, up to each lane's latest.
            AlertMetric.DesksBelowPlan => await inputs.LatestStaffingMinutesAsync(rule.SiteCode, zoneNames, ended, ct),
            _ => await inputs.LatestMinutesAsync(rule.SiteCode, zoneNames, ended, ct)
        };
        if (latest is null && AlertRuleValues.IsHealthCheck(values.Metric))
            return Skipped(rule, targets.Count, withdrawn, now);
        var plan = new List<(AlertTarget Target, AlertRuleState State, DateTime From, DateTime To)>();
        foreach (var target in targets)
        {
            if (!byTarget.TryGetValue((target.ZoneName, target.DeviceCode ?? string.Empty), out var state))
            {
                // A new target starts at the present (the minute that has just ended is its first).
                state = new AlertRuleState(ruleId, target.ZoneName, target.DeviceCode, version, now);
                state.Keep(AlertTargetState.Fresh with { LastMinuteUtc = ended.AddMinutes(-1) }, null, version, now);
                await Storage.SaveAsync(state, ct);
            }
            else if (!string.Equals(state.RuleValues, version, StringComparison.Ordinal))
            {
                // An edit restarts the counts. An open alert stays and clears under the new values, unless the rule now
                // watches another metric: then it is resolved and the target re-armed.
                if (state.OpenAlertId is { } id && await LockedAsync(id, ct) is { } alert && alert.Metric != rule.Metric)
                {
                    if (alert.IsOpen)
                    {
                        alert.Resolve(AlertResolution.RuleChanged, now, AlertEvaluation.SystemUserName);
                        Changed(alert);
                        withdrawn++;
                    }
                    state.Keep(AlertTargetState.Fresh with { LastMinuteUtc = state.LastMinuteUtc }, null, version, now);
                }
                else
                {
                    state.Keep(state.Evaluator with { Sustained = 0, Clearing = 0 }, state.OpenAlertId, version, now);
                }
            }

            DateTime upTo;
            if (latest is null)
                upTo = ended;
            else if (!latest.TryGetValue(target.ZoneName, out upTo))
                continue;
            var from = state.LastMinuteUtc ?? ended.AddMinutes(-1);
            if (from < upTo.AddMinutes(-maxCatchUpMinutes))
                from = upTo.AddMinutes(-maxCatchUpMinutes);
            if (from < upTo)
                plan.Add((target, state, from, upTo));
        }

        // Targets are read in clusters of nearby windows (each at most the catch-up long), so a target far behind the others
        // never stretches one read over the months between them for every target (ARV-114b, CWE-120).
        var seriesByKey = new Dictionary<string, AlertTargetSeries>(StringComparer.Ordinal);
        foreach (var cluster in AlertInputs.Clusters(plan, p => p.From, TimeSpan.FromMinutes(maxCatchUpMinutes)))
        {
            var series = await inputs.ReadAsync(rule.SiteCode, values, [.. cluster.Select(p => p.Target)], cluster.Min(p => p.From), cluster.Max(p => p.To), ct);
            if (series is null)
                return Skipped(rule, plan.Count, withdrawn, now);
            foreach (var read in series)
                seriesByKey[read.Target.Key] = read;
        }

        if (plan.Count > 0)
        {
            foreach (var (target, state, from, to) in plan)
            {
                var minutes = seriesByKey.TryGetValue(target.Key, out var s) ? s.Minutes.Where(m => m.MinuteUtc > from && m.MinuteUtc <= to) : [];
                var evaluator = state.Evaluator;
                var open = state.OpenAlertId;
                foreach (var minute in minutes)
                {
                    (evaluator, var transition) = AlertEvaluator.Step(values, evaluator, minute);
                    if (transition?.Kind == AlertTransitionKind.Raised)
                    {
                        var alert = new Alert(rule, target.ZoneName, target.DeviceCode, transition);
                        await Storage.SaveAsync(alert, ct);
                        Changed(alert);
                        // Written after the alert, so the flush inserts the alert first (the email refers to it).
                        await emails.EnqueueAsync(alert, EmailKind.AlertRaised, now, ct);
                        open = alert.Id;
                        raised++;
                    }
                    else if (transition?.Kind == AlertTransitionKind.Cleared)
                    {
                        if (open is { } id && await LockedAsync(id, ct) is { } alert && alert.IsOpen)
                        {
                            alert.Clear(transition);
                            Changed(alert);
                        }
                        // The session inserts before it updates: the resolve must reach the database before a later raise
                        // of the same target inserts its alert, or the one-open-alert index refuses it.
                        await Storage.FlushAsync(ct);
                        open = null;
                        cleared++;
                    }
                }

                state.Keep(evaluator, evaluator.Armed ? null : open, version, now);
            }
        }

        // Alerts first, then the states that refer to them.
        await Storage.FlushAsync(ct);
        Announce(now);
        unitOfWork.PromiseToCommit();
        return new AlertRuleTickResult(plan.Count, raised, cleared, withdrawn);
    }

    /// <summary>
    /// A rule whose inputs are beyond their read bound this tick (<see cref="AlertInputs.MaxHealthRows"/> for the health
    /// checks, <see cref="AlertInputs.MaxStaffingRows"/> for open desks below recommendation; CWE-120): its targets keep their
    /// states, so the next tick tries again from the same minutes; what the tick already changed (new targets, withdrawn
    /// alerts) is kept. One warning names the site and the rule code only.
    /// </summary>
    private AlertRuleTickResult Skipped(AlertRule rule, int targets, int withdrawn, DateTime now)
    {
        logger.LogWarning("Alert evaluation of site {Site} rule {Rule}: its stored inputs exceed the rows one read takes; the rule is not evaluated this tick",
            rule.SiteCode, rule.Code);
        Announce(now);
        unitOfWork.PromiseToCommit();
        return new AlertRuleTickResult(targets, 0, 0, withdrawn);
    }

    /// <summary>
    /// A stored rule the evaluation does not judge (ARV-181b2): nothing is read or changed, its targets keep their states and
    /// any open alert stays as it is. One warning names the site, the rule code and a fixed reason only (CWE-117: no value
    /// of the rule is logged).
    /// </summary>
    private AlertRuleTickResult NotEvaluated(AlertRule rule, DateTime now, string reason)
    {
        logger.LogWarning("Alert evaluation of site {Site} rule {Rule}: not evaluated, {Reason}", rule.SiteCode, rule.Code, reason);
        Announce(now);
        unitOfWork.PromiseToCommit();
        return new AlertRuleTickResult(0, 0, 0, 0);
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}

/// <summary>Runs <see cref="AlertEvaluation"/> every interval.</summary>
public sealed class AlertEvaluationWorker(AlertEvaluation evaluation, AlertEvaluationSettings settings, TimeProvider timeProvider, ILogger<AlertEvaluationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.IntervalSeconds), timeProvider);
        do
        {
            try
            {
                await evaluation.TickAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one failed tick is logged; the next tick tries again
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Alert evaluation tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
