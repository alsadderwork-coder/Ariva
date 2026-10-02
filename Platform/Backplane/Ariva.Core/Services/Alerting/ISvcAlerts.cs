using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Alerting;

/// <summary>The answers of the alert service that are not plain validation (ARV-039).</summary>
public static class AlertErrors
{
    public const string InvalidTransition = "The alert is not in a state for that action.";
    public const string InvalidNote = "A note is up to 500 characters without control or invisible characters; resolving needs one.";

    /// <summary>Answered 409 Conflict.</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { InvalidTransition };
}

/// <summary>
/// Alerts (ARV-039): read within the caller's sites and only where the caller's role is responsible (the alert's owner
/// role, its escalation role once escalated, every role when it has no owner, administrators always); otherwise an
/// alert answers like one that does not exist. Acknowledge, escalate and resolve by hand move it forward only, are
/// audited, and are announced to the live hub.
/// </summary>
public interface ISvcAlerts : ISvcScoped
{
    Task<Result<PageViewModel<AlertViewModel>>> SearchAsync(AlertCriteria criteria, CancellationToken ct = default);
    Task<Result<AlertViewModel>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<AlertViewModel>> AcknowledgeAsync(Guid id, AlertActionRequest request, CancellationToken ct = default);
    Task<Result<AlertViewModel>> EscalateAsync(Guid id, AlertActionRequest request, CancellationToken ct = default);
    Task<Result<AlertViewModel>> ResolveAsync(Guid id, AlertActionRequest request, CancellationToken ct = default);
}
