using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// An alert rule as a request (ARV-037): typed fields only, by name for the metric, comparator, severity and roles; no
/// expression of any kind (CWE-94). The site is given on creation and never changes; the code is given by the server.
/// </summary>
public sealed record AlertRuleRequest(
    [MaxLength(17)] string SiteCode,
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(64)] IReadOnlyList<string> Zones,
    [Required, MaxLength(32)] string Metric,
    [Required, MaxLength(32)] string Comparator,
    double? Threshold,
    int? MinQueueLength,
    double? ClearThreshold,
    [Range(1, 120)] int SustainMinutes,
    [Range(1, 120)] int ClearAfterMinutes,
    [Required, MaxLength(16)] string Severity,
    [MaxLength(32)] string OwnerRole,
    [Range(1, 1440)] int? EscalateAfterMinutes,
    [MaxLength(32)] string EscalateToRole,
    [MaxLength(100)] string EscalationContact,
    bool NotifyByEmail,
    bool Enabled = true);
