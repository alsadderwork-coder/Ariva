namespace Ariva.Core.Domain.ViewModels;

public sealed record AirportViewModel(Guid Id, string IataCode, string IcaoCode, string Name, string TimeZoneId, DateTime? CreatedOn);

public sealed record TerminalViewModel(Guid Id, Guid AirportId, string AirportIataCode, string Code, string Name, string SiteCode, DateTime? CreatedOn);

public sealed record LevelViewModel(Guid Id, Guid TerminalId, string Code, string Name, int FloorNumber, double WidthMetres, double DepthMetres, string SiteCode, DateTime? CreatedOn);

public sealed record CheckpointViewModel(Guid Id, Guid LevelId, string Code, string Name, string Kind, string SiteCode, DateTime? CreatedOn);

public sealed record DeskViewModel(Guid Id, Guid CheckpointId, string Code, string Name, string Kind, IReadOnlyList<string> LaneCategories, bool InService, string SiteCode, DateTime? CreatedOn);
