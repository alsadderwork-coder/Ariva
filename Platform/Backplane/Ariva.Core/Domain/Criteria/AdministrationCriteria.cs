using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.Criteria;

/// <summary>User search (ARV-011). Sort fields come from <see cref="SortFields"/> only (CWE-89).</summary>
public sealed record UserCriteria : BaseCriteria
{
    public static readonly IReadOnlyList<string> SortFields = ["userName", "displayName", "lastLoginOn", "createdOn"];

    /// <summary>Part of the username or display name.</summary>
    [MaxLength(64)]
    public string Text { get; set; }

    [MaxLength(64)]
    public string Role { get; set; }

    public bool? IsDisabled { get; set; }

    [MaxLength(32)]
    public string SortBy { get; set; }

    public bool SortDescending { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}

/// <summary>Audit search (ARV-011): newest first, by actor, target, action and time range (FromDate and ToDate, UTC).</summary>
public sealed record AuditEntryCriteria : BaseCriteria
{
    public Guid? ActorId { get; set; }

    public Guid? TargetId { get; set; }

    [MaxLength(64)]
    public string Action { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}
