namespace Ariva.Core.Domain.ViewModels;

public sealed record AirportViewModel(Guid Id, string IataCode, string IcaoCode, string Name, string TimeZoneId, DateTime? CreatedOn);

public sealed record TerminalViewModel(Guid Id, Guid AirportId, string AirportIataCode, string Code, string Name, string SiteCode, DateTime? CreatedOn);

public sealed record LevelViewModel(Guid Id, Guid TerminalId, string Code, string Name, int FloorNumber, double WidthMetres, double DepthMetres, string SiteCode, DateTime? CreatedOn);

public sealed record CheckpointViewModel(Guid Id, Guid LevelId, string Code, string Name, string Kind, string SiteCode, DateTime? CreatedOn);

public sealed record DeskViewModel(Guid Id, Guid CheckpointId, string Code, string Name, string Kind, IReadOnlyList<string> LaneCategories, bool InService, string SiteCode, DateTime? CreatedOn);

public sealed record DeskCodeMappingViewModel(Guid Id, string System, string ExternalCode, Guid DeskId, string DeskCode, string SiteCode, DateTime? CreatedOn);

public sealed record FloorPlanViewModel(Guid Id, Guid LevelId, string ContentType, long SizeBytes, string Sha256, string OriginalFileName,
    int? WidthPixels, int? HeightPixels, double MetresPerPixel, double OriginX, double OriginY, string SiteCode, DateTime? CreatedOn);

/// <summary>The stored plan bytes and how to serve them.</summary>
public sealed record FloorPlanContent(Stream Content, string ContentType, string Sha256);

/// <summary>An alert rule (ARV-037) as the API shows it.</summary>
public sealed record AlertRuleViewModel(
    Guid Id,
    string SiteCode,
    string Code,
    string Name,
    IReadOnlyList<string> Zones,
    string Metric,
    string Comparator,
    double? Threshold,
    int? MinQueueLength,
    double? ClearThreshold,
    int SustainMinutes,
    int ClearAfterMinutes,
    string Severity,
    string OwnerRole,
    int? EscalateAfterMinutes,
    string EscalateToRole,
    string EscalationContact,
    bool NotifyByEmail,
    bool Enabled,
    DateTime? CreatedOn,
    DateTime? ModifiedOn);
