using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.Criteria;

/// <summary>
/// Search over one topology entity (ARV-014): text in the code or name, the parent, paging and a sort field from
/// <see cref="SortFields"/> only (CWE-89).
/// </summary>
public sealed record TopologyCriteria : BaseCriteria
{
    public static readonly IReadOnlyList<string> SortFields = ["code", "name", "createdOn"];

    [MaxLength(64)]
    public string Text { get; set; }

    /// <summary>The parent: airport for terminals, terminal for levels, level for checkpoints, checkpoint for desks.</summary>
    public Guid? ParentId { get; set; }

    [MaxLength(17)]
    public string SiteCode { get; set; }

    [MaxLength(32)]
    public string SortBy { get; set; }

    public bool SortDescending { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}
