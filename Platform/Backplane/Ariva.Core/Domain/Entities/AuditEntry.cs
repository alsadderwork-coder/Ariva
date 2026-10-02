namespace Ariva.Core.Domain.Entities;

/// <summary>
/// One security-relevant administrative action (ARV-011): who did what to which record, when, from where, with short
/// before and after summaries. Append-only: the runtime login has no UPDATE or DELETE on audit_entry (script 0006) and
/// no API edits or deletes an entry. Summaries never carry secrets (passwords, TOTP secrets, recovery codes).
/// </summary>
public class AuditEntry : EntityBase<AuditEntry>
{
    public const int SummaryLength = 2000;

    protected AuditEntry()
    {
    }

    public AuditEntry(DateTime occurredOn, Guid? actorId, string actorName, string action, string targetType, Guid? targetId,
        string targetName, string beforeSummary, string afterSummary, string ipAddress, string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);
        if (occurredOn.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Audit times are UTC.", nameof(occurredOn));

        OccurredOn = occurredOn;
        ActorId = actorId;
        ActorName = Clip(actorName, 200);
        Action = Clip(action, 64);
        TargetType = Clip(targetType, 64);
        TargetId = targetId;
        TargetName = Clip(targetName, 200);
        BeforeSummary = Clip(beforeSummary, SummaryLength);
        AfterSummary = Clip(afterSummary, SummaryLength);
        IpAddress = Clip(ipAddress, 64);
        TraceId = Clip(traceId, 64);
    }

    public virtual DateTime OccurredOn { get; protected set; }
    public virtual Guid? ActorId { get; protected set; }
    public virtual string ActorName { get; protected set; }
    public virtual string Action { get; protected set; }
    public virtual string TargetType { get; protected set; }
    public virtual Guid? TargetId { get; protected set; }
    public virtual string TargetName { get; protected set; }
    [System.ComponentModel.DataAnnotations.MaxLength(SummaryLength)]
    public virtual string BeforeSummary { get; protected set; }

    [System.ComponentModel.DataAnnotations.MaxLength(SummaryLength)]
    public virtual string AfterSummary { get; protected set; }
    public virtual string IpAddress { get; protected set; }
    public virtual string TraceId { get; protected set; }

    private static string Clip(string value, int length) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= length ? value : value[..length];
}
