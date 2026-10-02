using Ariva.Core.Alerting;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// An alert (glossary Alert): raised by a rule on one target (a queue zone, or a device of it for a sensor rule) when
/// the rule's condition held for its sustain minutes, resolved when its clear condition held for its clear minutes or
/// the rule was withdrawn (ARV-038). It keeps what the rule said when it was raised (code, name, severity, owner and
/// escalation), so a later edit of the rule does not rewrite history. One open alert per rule and target.
/// Acknowledging and escalating are ARV-039.
/// </summary>
public class Alert : EntityBase<Alert>, ISiteBound
{
    public const string AutomaticResolver = "alert-evaluation";

    /// <summary>The largest value an alert records (the table's range); a larger one is recorded at this bound.</summary>
    public const double MaxRecordedValue = 1e9;

    protected Alert()
    {
    }

    public Alert(AlertRule rule, string zoneName, string deviceCode, AlertTransition raised)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(raised);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneName);
        if (raised.Kind != AlertTransitionKind.Raised || raised.MinuteUtc.Kind != DateTimeKind.Utc || !double.IsFinite(raised.Value))
            throw new ArgumentException("An alert starts from a raise at a UTC minute with a finite value.", nameof(raised));
        Id = NewId();
        SiteCode = rule.SiteCode;
        RuleId = rule.Id ?? throw new ArgumentException("The rule must be saved.", nameof(rule));
        RuleCode = rule.Code;
        RuleName = rule.Name;
        ZoneName = zoneName;
        DeviceCode = deviceCode;
        Metric = rule.Metric;
        Severity = rule.Severity;
        OwnerRole = rule.OwnerRole;
        EscalateAfterMinutes = rule.EscalateAfterMinutes;
        EscalateToRole = rule.EscalateToRole;
        EscalationContact = rule.EscalationContact;
        RaisedUtc = raised.MinuteUtc;
        RaisedValue = Math.Clamp(raised.Value, -MaxRecordedValue, MaxRecordedValue);
        BinStartUtc = raised.BinStartUtc;
        PredictedForUtc = raised.PredictedForUtc;
        State = AlertState.Raised;
    }

    public virtual string SiteCode { get; protected set; }
    public virtual Guid RuleId { get; protected set; }
    public virtual string RuleCode { get; protected set; }
    public virtual string RuleName { get; protected set; }
    public virtual string ZoneName { get; protected set; }

    /// <summary>The device of a sensor rule's alert; null for a zone's.</summary>
    public virtual string DeviceCode { get; protected set; }

    public virtual AlertMetric Metric { get; protected set; }
    public virtual AlertSeverity Severity { get; protected set; }
    public virtual string OwnerRole { get; protected set; }
    public virtual int? EscalateAfterMinutes { get; protected set; }
    public virtual string EscalateToRole { get; protected set; }
    public virtual string EscalationContact { get; protected set; }

    /// <summary>The end of the minute whose value raised it.</summary>
    public virtual DateTime RaisedUtc { get; protected set; }

    public virtual double RaisedValue { get; protected set; }

    /// <summary>The bin a P90 alert is about.</summary>
    public virtual DateTime? BinStartUtc { get; protected set; }

    /// <summary>The minute a predicted breach is projected for.</summary>
    public virtual DateTime? PredictedForUtc { get; protected set; }

    public virtual AlertState State { get; protected set; }

    /// <summary>The minute the clear condition completed, for an alert that cleared by itself.</summary>
    public virtual DateTime? ClearedUtc { get; protected set; }

    public virtual DateTime? ResolvedUtc { get; protected set; }
    public virtual AlertResolution? Resolution { get; protected set; }
    public virtual string ResolvedBy { get; protected set; }

    public virtual bool IsOpen => State != AlertState.Resolved;

    /// <summary>Resolves it because its clear condition held (auto-resolve).</summary>
    public virtual void Clear(AlertTransition cleared)
    {
        ArgumentNullException.ThrowIfNull(cleared);
        if (cleared.Kind != AlertTransitionKind.Cleared || cleared.MinuteUtc < RaisedUtc)
            throw new ArgumentException("A clear follows the raise.", nameof(cleared));
        if (!IsOpen)
            return;
        ClearedUtc = cleared.MinuteUtc;
        Resolve(AlertResolution.Cleared, cleared.MinuteUtc, AutomaticResolver);
    }

    /// <summary>Resolves an open alert; a resolved one stays as it was.</summary>
    public virtual void Resolve(AlertResolution resolution, DateTime utc, string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (utc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Times are UTC.", nameof(utc));
        if (!IsOpen)
            return;
        State = AlertState.Resolved;
        Resolution = resolution;
        ResolvedUtc = utc < RaisedUtc ? RaisedUtc : utc;
        ResolvedBy = by.Length > 200 ? by[..200] : by;
    }
}

/// <summary>
/// Where the live evaluation stands for one rule and target between ticks (ARV-038): the evaluator's state, the open
/// alert while disarmed, and the hash of the rule's values it was evaluated under (an edit restarts the counts).
/// </summary>
public class AlertRuleState : EntityBase<AlertRuleState>
{
    protected AlertRuleState()
    {
    }

    public AlertRuleState(Guid ruleId, string zoneName, string deviceCode, string ruleValues, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneName);
        Id = NewId();
        RuleId = ruleId;
        ZoneName = zoneName;
        DeviceCode = deviceCode ?? string.Empty;
        Armed = true;
        RuleValues = ruleValues;
        UpdatedOn = utcNow;
    }

    public virtual Guid RuleId { get; protected set; }
    public virtual string ZoneName { get; protected set; }

    /// <summary>The device of a sensor rule's target; empty for a zone.</summary>
    public virtual string DeviceCode { get; protected set; }

    public virtual bool Armed { get; protected set; }
    public virtual int Sustained { get; protected set; }
    public virtual int Clearing { get; protected set; }
    public virtual DateTime? LastMinuteUtc { get; protected set; }
    public virtual Guid? OpenAlertId { get; protected set; }
    /// <summary>The hash of the rule's values (<see cref="AlertRule.ValuesHash"/>) the state was kept under.</summary>
    public virtual string RuleValues { get; protected set; }
    public virtual DateTime UpdatedOn { get; protected set; }

    public virtual AlertTargetState Evaluator => new(Armed, Sustained, Clearing, LastMinuteUtc);

    /// <summary>Keeps the evaluator's state after a tick, with the alert open on the target (null when armed).</summary>
    public virtual void Keep(AlertTargetState state, Guid? openAlertId, string ruleValues, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Armed != (openAlertId is null))
            throw new ArgumentException("A target is armed exactly when it has no open alert.", nameof(openAlertId));
        (Armed, Sustained, Clearing, LastMinuteUtc) = (state.Armed, Math.Clamp(state.Sustained, 0, 1440), Math.Clamp(state.Clearing, 0, 1440), state.LastMinuteUtc);
        OpenAlertId = openAlertId;
        RuleValues = ruleValues;
        UpdatedOn = utcNow;
    }
}
