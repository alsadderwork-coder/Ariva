using System.Globalization;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// An alert rule (ARV-037, glossary Alert): typed data, never an expression. A metric of the queue zones in scope is
/// compared with a threshold in the metric's unit; the condition must hold for the sustain minutes (with an optional
/// minimum queue length for the nowcast) to raise, and stop holding (or fall below the clear threshold) for the clear
/// minutes to clear. The owner role acts on it (none: the zone's owner), and an unacknowledged alert escalates after the
/// escalation minutes to a role or a named contact. Every value is checked here, so no request can store a rule the
/// evaluator (ARV-038) cannot apply. Codes R-001, R-002 and so on are given per site in order.
/// </summary>
public class AlertRule : BaseSoftDeletableEntity<AlertRule>, ISiteBound
{
    public const int MaxNameLength = 200;
    public const int MaxZones = 64;
    public const int MaxContactLength = 100;
    public const int MaxMinutes = 120;
    public const int MaxEscalationMinutes = 1440;
    public const int MinLeadMinutes = 15;
    public const int MaxLeadMinutes = 60;

    /// <summary>The last number a site's codes reach (R-999999, the table's check); deleted rules keep theirs.</summary>
    public const int MaxNumber = 999_999;

    protected AlertRule()
    {
    }

    public AlertRule(string siteCode, int number, AlertRuleValues values)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxNumber);
        SiteCode = siteCode;
        Code = CodeOf(number);
        Set(values);
    }

    public virtual string SiteCode { get; protected set; }
    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }

    /// <summary>The queue zones in scope (zones of the site's published profile), one name per line.</summary>
    public virtual string ScopeZones { get; protected set; }

    public virtual AlertMetric Metric { get; protected set; }
    public virtual AlertComparator Comparator { get; protected set; }
    public virtual double? Threshold { get; protected set; }
    public virtual int? MinQueueLength { get; protected set; }
    public virtual double? ClearThreshold { get; protected set; }
    public virtual int SustainMinutes { get; protected set; }
    public virtual int ClearAfterMinutes { get; protected set; }
    public virtual AlertSeverity Severity { get; protected set; }

    /// <summary>The role that owns the alerts; null for the owner of each zone.</summary>
    public virtual string OwnerRole { get; protected set; }

    public virtual int? EscalateAfterMinutes { get; protected set; }

    /// <summary>The role an unacknowledged alert escalates to; null for the zone owner's escalation path.</summary>
    public virtual string EscalateToRole { get; protected set; }

    /// <summary>Who the escalation reaches outside Ariva's roles, as people read it (for example "Border operations duty officer").</summary>
    public virtual string EscalationContact { get; protected set; }

    /// <summary>Email the owner as well as the screen (the screen always shows alerts; SMS and webhooks are v1).</summary>
    public virtual bool NotifyByEmail { get; protected set; }

    public virtual bool Enabled { get; protected set; }

    /// <summary>How far ahead a <see cref="AlertMetric.PredictedNowcast"/> rule looks (15 to 60 minutes); null otherwise.</summary>
    public virtual int? LeadMinutes { get; protected set; }

    public virtual IReadOnlyList<string> Zones => ScopeZones is null ? [] : ScopeZones.Split('\n');

    public static string CodeOf(int number) => "R-" + number.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>Replaces the rule's values after checking them (see <see cref="AlertRuleValues.Problems"/>).</summary>
    public virtual void Set(AlertRuleValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = values.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(values));
        Name = values.Name.Trim();
        ScopeZones = string.Join('\n', values.Zones.Select(z => z.Trim()).Distinct(StringComparer.Ordinal));
        Metric = values.Metric;
        Comparator = values.Comparator;
        Threshold = values.Threshold;
        MinQueueLength = values.MinQueueLength;
        ClearThreshold = values.ClearThreshold;
        SustainMinutes = values.SustainMinutes;
        ClearAfterMinutes = values.ClearAfterMinutes;
        Severity = values.Severity;
        OwnerRole = values.OwnerRole;
        EscalateAfterMinutes = values.EscalateAfterMinutes;
        EscalateToRole = values.EscalateToRole;
        EscalationContact = string.IsNullOrWhiteSpace(values.EscalationContact) ? null : values.EscalationContact.Trim();
        NotifyByEmail = values.NotifyByEmail;
        Enabled = values.Enabled;
        LeadMinutes = values.LeadMinutes;
    }

    /// <summary>
    /// The rule's values for the audit trail, as JSON (names and contacts cannot pass for other fields): every fixed field
    /// first, then the zones' count and SHA-256, the zone list last, so a summary the trail clips loses zone names only.
    /// </summary>
    public virtual string AuditSummary()
    {
        var zones = string.Join('\n', Zones);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            site = SiteCode,
            code = Code,
            name = Name,
            metric = Metric.ToString(),
            comparator = Comparator.ToString(),
            threshold = Threshold,
            minQueue = MinQueueLength,
            clear = ClearThreshold,
            sustain = SustainMinutes,
            clearAfter = ClearAfterMinutes,
            severity = Severity.ToString(),
            owner = OwnerRole,
            escalateAfter = EscalateAfterMinutes,
            escalateTo = EscalateToRole,
            contact = EscalationContact,
            email = NotifyByEmail,
            enabled = Enabled,
            lead = LeadMinutes,
            zoneCount = Zones.Count,
            zonesSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(zones))),
            zones = Zones
        });
    }

    /// <summary>SHA-256 of <see cref="AuditSummary"/>: changes whenever any of the rule's values changes.</summary>
    public virtual string ValuesHash() =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AuditSummary())));

    public virtual AlertRuleValues Values() => new(Name, Zones, Metric, Comparator, Threshold, MinQueueLength, ClearThreshold, SustainMinutes, ClearAfterMinutes,
        Severity, OwnerRole, EscalateAfterMinutes, EscalateToRole, EscalationContact, NotifyByEmail, Enabled, LeadMinutes);
}

/// <summary>Everything an alert rule says, as one value; <see cref="Problems"/> lists what makes it unusable.</summary>
public sealed record AlertRuleValues(
    string Name,
    IReadOnlyList<string> Zones,
    AlertMetric Metric,
    AlertComparator Comparator,
    double? Threshold,
    int? MinQueueLength,
    double? ClearThreshold,
    int SustainMinutes,
    int ClearAfterMinutes,
    AlertSeverity Severity,
    string OwnerRole,
    int? EscalateAfterMinutes,
    string EscalateToRole,
    string EscalationContact,
    bool NotifyByEmail,
    bool Enabled,
    int? LeadMinutes = null)
{
    /// <summary>The largest threshold per unit: minutes for waits, people for queue length, desks for desks below plan.</summary>
    public static double MaxThreshold(AlertMetric metric) => metric switch
    {
        AlertMetric.Nowcast or AlertMetric.BinP90 or AlertMetric.PredictedNowcast => 600,
        AlertMetric.QueueLength => 100_000,
        AlertMetric.DesksBelowPlan => 1_000,
        _ => 0
    };

    /// <summary>Metrics that are true or false, compared with <see cref="AlertComparator.IsTrue"/> and no threshold.</summary>
    public static bool IsCondition(AlertMetric metric) => metric is AlertMetric.OverflowOccupied or AlertMetric.SensorOffline;

    private static bool IsRole(string code) => RoleCodes.All.Any(r => string.Equals(r, code, StringComparison.Ordinal));

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > AlertRule.MaxNameLength || !DisplayText.IsClean(Name.Trim()))
            problems.Add($"A name is 1 to {AlertRule.MaxNameLength} characters without control or invisible characters.");
        var zones = (Zones ?? []).Select(z => z?.Trim()).ToList();
        if (zones.Count is 0 or > AlertRule.MaxZones)
            problems.Add($"A rule watches 1 to {AlertRule.MaxZones} queue zones.");
        if (zones.Any(z => string.IsNullOrEmpty(z) || z.Length > 200 || !DisplayText.IsClean(z)))
            problems.Add("A zone name is 1 to 200 characters without control or invisible characters.");
        if (!Enum.IsDefined(Metric) || !Enum.IsDefined(Comparator) || !Enum.IsDefined(Severity))
            problems.Add("Unknown metric, comparator or severity.");
        else if (IsCondition(Metric))
        {
            if (Comparator != AlertComparator.IsTrue || Threshold is not null || ClearThreshold is not null || MinQueueLength is not null)
                problems.Add($"{Metric} is true or false: compare with IsTrue, without a threshold, clear threshold or minimum queue.");
        }
        else
        {
            var max = MaxThreshold(Metric);
            if (Comparator == AlertComparator.IsTrue)
                problems.Add($"{Metric} is compared with a threshold.");
            if (Threshold is not { } t || !double.IsFinite(t) || t < 0 || t > max)
                problems.Add($"The threshold of {Metric} is from 0 to {max}.");
            if (ClearThreshold is { } c && Threshold is { } th)
            {
                var above = Comparator is AlertComparator.GreaterThan or AlertComparator.GreaterOrEqual;
                if (!double.IsFinite(c) || c < 0 || c > max || (above ? c >= th : c <= th))
                    problems.Add("The clear threshold lies on the clear side of the threshold (below it for a rule that fires above).");
            }

            if (MinQueueLength is { } q && (Metric != AlertMetric.Nowcast || q is < 0 or > 100_000))
                problems.Add("A minimum queue length (0 to 100,000 people) applies to the nowcast only.");
        }

        if (Metric == AlertMetric.PredictedNowcast
                ? LeadMinutes is not { } lead || lead < AlertRule.MinLeadMinutes || lead > AlertRule.MaxLeadMinutes
                : LeadMinutes is not null)
            problems.Add($"A predicted nowcast looks {AlertRule.MinLeadMinutes} to {AlertRule.MaxLeadMinutes} minutes ahead; other metrics take no lead time.");

        if (SustainMinutes is < 1 or > AlertRule.MaxMinutes || ClearAfterMinutes is < 1 or > AlertRule.MaxMinutes)
            problems.Add($"Sustain and clear are 1 to {AlertRule.MaxMinutes} minutes.");
        if (OwnerRole is not null && !IsRole(OwnerRole))
            problems.Add("Unknown owner role.");
        if (EscalateToRole is not null && !IsRole(EscalateToRole))
            problems.Add("Unknown escalation role.");
        if (EscalateAfterMinutes is { } after && after is < 1 or > AlertRule.MaxEscalationMinutes)
            problems.Add($"Escalation is after 1 to {AlertRule.MaxEscalationMinutes} minutes.");
        if (EscalateAfterMinutes is null && (EscalateToRole is not null || !string.IsNullOrWhiteSpace(EscalationContact)))
            problems.Add("An escalation target needs the minutes after which it applies.");
        if (!string.IsNullOrWhiteSpace(EscalationContact) &&
            (EscalationContact.Trim().Length > AlertRule.MaxContactLength || !DisplayText.IsClean(EscalationContact.Trim())))
            problems.Add($"An escalation contact is at most {AlertRule.MaxContactLength} characters without control or invisible characters.");
        return problems;
    }
}
