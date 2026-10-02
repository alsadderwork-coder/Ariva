using System.Data;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
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

/// <summary>What one tick did; <see cref="Failed"/> counts rules whose evaluation failed (each logged).</summary>
public sealed record AlertTickResult(bool Ran, int Rules, int Targets, int Raised, int Cleared, int Withdrawn, int Failed = 0);

/// <summary>What one rule's evaluation did in a tick.</summary>
public sealed record AlertRuleTickResult(int Targets, int Raised, int Cleared, int Withdrawn);

/// <summary>
/// The live evaluation of alert rules (ARV-038), one tick a minute. A tick holds an advisory lock in a transaction of
/// its own for its whole run, so one replica evaluates and the others skip; it then withdraws the alerts of disabled
/// and deleted rules and evaluates each enabled rule in its own scope and transaction (<see cref="AlertRuleTick"/>),
/// so a rule that fails is logged and retried at the next tick without holding up the others.
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

            int targets = 0, raised = 0, cleared = 0, failed = 0, withdrawn = 0;
            try
            {
                withdrawn = await InScopeAsync(tick => tick.WithdrawAsync(now, ct));
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
                logger.LogError(e, "Withdrawing the alerts of disabled and deleted rules failed; it is tried again at the next tick");
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

            if (raised + cleared + withdrawn + failed > 0)
                logger.LogInformation("Alert evaluation: {Raised} raised, {Cleared} cleared, {Withdrawn} withdrawn, {Failed} failed over {Targets} targets of {Rules} rules",
                    raised, cleared, withdrawn, failed, targets, ruleIds.Count);
            return new AlertTickResult(true, ruleIds.Count, targets, raised, cleared, withdrawn, failed);
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
public sealed class AlertRuleTick(IUnitOfWork unitOfWork, ICurrentUser currentUser, AlertInputs inputs)
{
    private IStorageProvider Storage => unitOfWork.StorageProvider;

    private void Begin()
    {
        currentUser.SetSystemUser(Guid.Empty, AlertEvaluation.SystemUserName);
        if (!Storage.IsTransactionActive())
            Storage.BeginTransaction(IsolationLevel.ReadCommitted);
    }

    /// <summary>
    /// Resolves the open alerts of disabled and deleted rules and drops those rules' states (the session hides deleted
    /// rules, so both are found with SQL).
    /// </summary>
    public async Task<int> WithdrawAsync(DateTime now, CancellationToken ct)
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
            if (await Storage.GetAsync<Alert>(row.Id, ct) is { } alert)
                alert.Resolve(AlertResolution.RuleWithdrawn, now, AlertEvaluation.SystemUserName);
        unitOfWork.PromiseToCommit();
        return open.Count;
    }

    public async Task<AlertRuleTickResult> EvaluateAsync(Guid ruleId, DateTime now, int maxCatchUpMinutes, CancellationToken ct)
    {
        Begin();
        var rule = await Storage.GetAsync<AlertRule>(ruleId, ct);
        if (rule is null || rule.IsDeleted || !rule.Enabled)
            return new AlertRuleTickResult(0, 0, 0, 0);
        var values = rule.Values();
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
            if (gone.OpenAlertId is { } id && await Storage.GetAsync<Alert>(id, ct) is { } alert && alert.IsOpen)
            {
                alert.Resolve(AlertResolution.TargetWithdrawn, now, AlertEvaluation.SystemUserName);
                withdrawn++;
            }

            await Storage.DeleteAsync(gone, ct);
            states.Remove(gone);
        }

        var byTarget = states.ToDictionary(s => (s.ZoneName, s.DeviceCode));
        var latest = values.Metric == AlertMetric.SensorOffline
            ? null
            : await inputs.LatestMinutesAsync(rule.SiteCode, [.. targets.Select(t => t.ZoneName).Distinct(StringComparer.Ordinal)], ended, ct);
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
                if (state.OpenAlertId is { } id && await Storage.GetAsync<Alert>(id, ct) is { } alert && alert.Metric != rule.Metric)
                {
                    alert.Resolve(AlertResolution.RuleChanged, now, AlertEvaluation.SystemUserName);
                    withdrawn++;
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

        if (plan.Count > 0)
        {
            var series = await inputs.ReadAsync(rule.SiteCode, values, [.. plan.Select(p => p.Target)], plan.Min(p => p.From), plan.Max(p => p.To), ct);
            var seriesByKey = series.ToDictionary(s => s.Target.Key, StringComparer.Ordinal);
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
                        open = alert.Id;
                        raised++;
                    }
                    else if (transition?.Kind == AlertTransitionKind.Cleared)
                    {
                        if (open is { } id && await Storage.GetAsync<Alert>(id, ct) is { } alert)
                            alert.Clear(transition);
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
        unitOfWork.PromiseToCommit();
        return new AlertRuleTickResult(plan.Count, raised, cleared, withdrawn);
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
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
