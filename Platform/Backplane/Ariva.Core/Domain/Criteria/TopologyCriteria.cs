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

/// <summary>Desk code mapping search (ARV-015): by system, site, desk and code text.</summary>
public sealed record DeskCodeMappingCriteria : BaseCriteria
{
    [MaxLength(16)]
    public string System { get; set; }

    [MaxLength(17)]
    public string SiteCode { get; set; }

    public Guid? DeskId { get; set; }

    [MaxLength(32)]
    public string Text { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}

/// <summary>Device search (ARV-021): code or model text, site, level, state, owning queue zone; sorted by code.</summary>
public sealed record DeviceCriteria : BaseCriteria
{
    [MaxLength(64)]
    public string Text { get; set; }

    [MaxLength(17)]
    public string SiteCode { get; set; }

    public Guid? LevelId { get; set; }

    [MaxLength(32)]
    public string State { get; set; }

    [MaxLength(200)]
    public string QueueZoneName { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}

/// <summary>Alert rule search (ARV-037): by site, metric, enabled and text in the code or name.</summary>
public sealed record AlertRuleCriteria : BaseCriteria
{
    [MaxLength(17)]
    public string SiteCode { get; set; }

    [MaxLength(32)]
    public string Metric { get; set; }

    public bool? Enabled { get; set; }

    [MaxLength(64)]
    public string Text { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}

/// <summary>Search over alerts (ARV-039): site, state (by name) or open only, zone, rule code and a raise-time range.</summary>
public sealed record AlertCriteria : BaseCriteria
{
    [MaxLength(17)]
    public string SiteCode { get; set; }

    [MaxLength(16)]
    public string State { get; set; }

    /// <summary>True for open alerts only (not resolved), false for resolved ones only.</summary>
    public bool? Open { get; set; }

    [MaxLength(200)]
    public string ZoneName { get; set; }

    [MaxLength(8)]
    public string RuleCode { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}
