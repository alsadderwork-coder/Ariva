using Ariva.Core.Alerting;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// An alert (glossary Alert): raised by a rule on one target (a queue zone, or a device of it for a sensor rule) when
/// the rule's condition held for its sustain minutes, resolved when its clear condition held for its clear minutes or
/// the rule was withdrawn (ARV-038). It keeps what the rule said when it was raised (code, name, severity, owner and
/// escalation), so a later edit of the rule does not rewrite history. One open alert per rule and target.
/// Its life (ARV-039): Raised, then Acknowledged by someone responsible, Escalated (by hand, or by the evaluation when
/// it stayed unacknowledged for the rule's escalation minutes), Resolved by hand with a note or by itself when it
/// clears. An acknowledgement and an escalation happen once each and are never rewritten; a resolved alert stays
/// resolved. Who is responsible is <see cref="ResponsibleRoles"/>.
/// </summary>
public class Alert : EntityBase<Alert>, ISiteBound
{
    public const string AutomaticResolver = "alert-evaluation";

    /// <summary>The largest value an alert records (the table's range); a larger one is recorded at this bound.</summary>
    public const double MaxRecordedValue = 1e9;

    /// <summary>The longest note on an acknowledgement, an escalation or a resolution.</summary>
    public const int MaxNoteLength = 500;

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

    public virtual DateTime? AcknowledgedUtc { get; protected set; }
    public virtual string AcknowledgedBy { get; protected set; }

    [System.ComponentModel.DataAnnotations.MaxLength(MaxNoteLength)]
    public virtual string AcknowledgedNote { get; protected set; }

    public virtual DateTime? EscalatedUtc { get; protected set; }
    public virtual string EscalatedBy { get; protected set; }

    [System.ComponentModel.DataAnnotations.MaxLength(MaxNoteLength)]
    public virtual string EscalatedNote { get; protected set; }

    [System.ComponentModel.DataAnnotations.MaxLength(MaxNoteLength)]
    public virtual string ResolutionNote { get; protected set; }

    public virtual bool IsOpen => State != AlertState.Resolved;

    /// <summary>
    /// The roles that see the alert and act on it, besides administrators: its owner role, and once escalated its
    /// escalation role; null when it has no owner role (the zone's owner), in which case every role of the site with
    /// the alert permissions does.
    /// </summary>
    public virtual IReadOnlySet<string> ResponsibleRoles =>
        OwnerRole is null ? null : new HashSet<string>(new[] { OwnerRole, EscalatedUtc is not null ? EscalateToRole : null }.Where(r => r is not null), StringComparer.Ordinal);

    /// <summary>Whether a caller holding these roles sees the alert and may act on it.</summary>
    public virtual bool IsFor(IEnumerable<string> roles)
    {
        var held = (roles ?? []).ToHashSet(StringComparer.Ordinal);
        return held.Contains(RoleCodes.SystemAdministrator) || ResponsibleRoles is not { } responsible || responsible.Overlaps(held);
    }

    /// <summary>
    /// Someone responsible takes it on: from Raised or Escalated, once (an alert acknowledged and then escalated was handed
    /// over; the escalation role resolves it). False when it is not in a state to acknowledge.
    /// </summary>
    public virtual bool Acknowledge(string by, DateTime utc, string note)
    {
        CheckAction(by, utc, note, required: false);
        if (State is not (AlertState.Raised or AlertState.Escalated) || AcknowledgedUtc is not null)
            return false;
        State = AlertState.Acknowledged;
        AcknowledgedUtc = Later(utc);
        AcknowledgedBy = Clip(by);
        AcknowledgedNote = Note(note);
        return true;
    }

    /// <summary>
    /// Escalates it to the rule's escalation role or contact: from Raised or Acknowledged, once. The evaluation does it
    /// when the alert stayed Raised for the escalation minutes. False when it is not in a state to escalate.
    /// </summary>
    public virtual bool Escalate(string by, DateTime utc, string note)
    {
        CheckAction(by, utc, note, required: false);
        if (State is not (AlertState.Raised or AlertState.Acknowledged) || EscalatedUtc is not null)
            return false;
        State = AlertState.Escalated;
        EscalatedUtc = Later(utc);
        EscalatedBy = Clip(by);
        EscalatedNote = Note(note);
        return true;
    }

    /// <summary>Whether it is due for escalation at <paramref name="utc"/>: still Raised past its escalation minutes.</summary>
    public virtual bool IsDueForEscalation(DateTime utc) =>
        State == AlertState.Raised && EscalatedUtc is null && EscalateAfterMinutes is { } minutes && utc >= RaisedUtc.AddMinutes(minutes);

    /// <summary>Resolved by hand, with a note saying why. False when it is already resolved.</summary>
    public virtual bool ResolveManually(string by, DateTime utc, string note)
    {
        CheckAction(by, utc, note, required: true);
        if (!IsOpen)
            return false;
        Resolve(AlertResolution.Manual, utc, by);
        ResolutionNote = Note(note);
        return true;
    }

    /// <summary>Whether a note is acceptable: up to 500 characters without control or invisible characters (line breaks allowed).</summary>
    public static bool IsValidNote(string note, bool required) =>
        string.IsNullOrWhiteSpace(note) ? !required : note.Trim().Length <= MaxNoteLength && Components.DisplayText.IsClean(note.Trim(), allowLineBreaks: true);

    private static void CheckAction(string by, DateTime utc, string note, bool required)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (utc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Times are UTC.", nameof(utc));
        if (!IsValidNote(note, required))
            throw new ArgumentException($"A note is {(required ? "1" : "0")} to {MaxNoteLength} characters without control or invisible characters.", nameof(note));
    }

    private DateTime Later(DateTime utc) => utc < RaisedUtc ? RaisedUtc : utc;

    private static string Clip(string by) => by.Length > 200 ? by[..200] : by;

    private static string Note(string note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

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
