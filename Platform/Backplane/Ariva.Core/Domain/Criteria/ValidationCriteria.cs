using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.Criteria;

/// <summary>
/// Search over a site's validation campaigns (ARV-104a): text in the name, a status from <see cref="Statuses"/>, created in
/// [FromDate, ToDate] (UTC), a sort field from <see cref="SortFields"/> only (CWE-89) and pages of at most 500.
/// </summary>
public sealed record ValidationCampaignCriteria : BaseCriteria
{
    /// <summary>firstDay orders by the first planned day (the stored days start with it).</summary>
    public static readonly IReadOnlyList<string> SortFields = ["name", "createdUtc", "status", "firstDay"];

    public static readonly IReadOnlyList<string> Statuses = ["Planned", "Running", "Closed"];

    [MaxLength(64)]
    public string Text { get; set; }

    [MaxLength(16)]
    public string Status { get; set; }

    [MaxLength(32)]
    public string SortBy { get; set; }

    public bool SortDescending { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// Search over a campaign's manual counts (ARV-104a): a line, an observer (ignored for an observer reading its own counts),
/// bins starting in [FromDate, ToDate) (UTC), the current revisions only (default) or every revision, a sort field from
/// <see cref="SortFields"/> only (CWE-89) and pages of at most 500.
/// </summary>
public sealed record ManualCountCriteria : BaseCriteria
{
    public static readonly IReadOnlyList<string> SortFields = ["binStartUtc", "recordedUtc"];

    public Guid? LineId { get; set; }

    public Guid? ObserverId { get; set; }

    /// <summary>True (default) for the current revision of each line, bin and observer; false for every revision.</summary>
    public bool CurrentOnly { get; set; } = true;

    [MaxLength(32)]
    public string SortBy { get; set; }

    public bool SortDescending { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 100;
}

/// <summary>
/// Search over a campaign's tracer runs (ARV-104b): a queue zone, an observer (ignored for an observer reading its own runs),
/// a tracer code, runs joined in [FromDate, ToDate) (UTC, server clock), a sort field from <see cref="SortFields"/> only
/// (CWE-89) and pages of at most 500.
/// </summary>
public sealed record TracerRunCriteria : BaseCriteria
{
    public static readonly IReadOnlyList<string> SortFields = ["joinedUtc", "recordedUtc"];

    public Guid? ZoneId { get; set; }

    public Guid? ObserverId { get; set; }

    [MaxLength(8)]
    public string TracerCode { get; set; }

    [MaxLength(32)]
    public string SortBy { get; set; }

    public bool SortDescending { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 100;
}

/// <summary>
/// Search over a campaign's desk observations (ARV-104b): a desk, an observer (ignored for an observer reading its own),
/// minutes in [FromDate, ToDate) (UTC), the current revisions only (default) or every revision, a sort field from
/// <see cref="SortFields"/> only (CWE-89) and pages of at most 500.
/// </summary>
public sealed record DeskObservationCriteria : BaseCriteria
{
    public static readonly IReadOnlyList<string> SortFields = ["minuteUtc", "recordedUtc"];

    public Guid? DeskId { get; set; }

    public Guid? ObserverId { get; set; }

    /// <summary>True (default) for the current revision of each desk, minute and observer; false for every revision.</summary>
    public bool CurrentOnly { get; set; } = true;

    [MaxLength(32)]
    public string SortBy { get; set; }

    public bool SortDescending { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    [Range(1, 500)]
    public int PageSize { get; set; } = 100;
}
