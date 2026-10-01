using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Topology;

/// <summary>Errors the topology services return; the controllers map them to status codes.</summary>
public static class TopologyErrors
{
    public const string NotFound = "The record does not exist.";
    public const string Duplicate = "A record with that code already exists here.";
    public const string HasChildren = "Delete the records under it first.";
    public const string UnknownKind = "Unknown kind.";
    public const string UnknownSite = "Unknown site.";
    public const string InvalidCriteria = "The search criteria are not valid.";
    public const string DeploymentWide = "Only an administrator with access to every site can change airports.";

    /// <summary>Errors that mean "already exists or in use" (409).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { Duplicate, HasChildren };
}

/// <summary>
/// Topology administration (ARV-014): airports, terminals, levels, checkpoints and desks. Reads and writes below the
/// airport are limited to the caller's sites, and a record outside them answers like one that does not exist. Every
/// change is audited. Deletes are soft and refused while live children exist.
/// </summary>
public interface ISvcTopology : ISvcScoped
{
    Task<Result<PageViewModel<AirportViewModel>>> SearchAirportsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<AirportViewModel>> GetAirportAsync(Guid id, CancellationToken ct = default);
    Task<Result<AirportViewModel>> CreateAirportAsync(CreateAirportRequest request, CancellationToken ct = default);
    Task<Result<AirportViewModel>> UpdateAirportAsync(Guid id, UpdateAirportRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteAirportAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<TerminalViewModel>>> SearchTerminalsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<TerminalViewModel>> GetTerminalAsync(Guid id, CancellationToken ct = default);
    Task<Result<TerminalViewModel>> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct = default);
    Task<Result<TerminalViewModel>> UpdateTerminalAsync(Guid id, UpdateTerminalRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteTerminalAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<LevelViewModel>>> SearchLevelsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<LevelViewModel>> GetLevelAsync(Guid id, CancellationToken ct = default);
    Task<Result<LevelViewModel>> CreateLevelAsync(CreateLevelRequest request, CancellationToken ct = default);
    Task<Result<LevelViewModel>> UpdateLevelAsync(Guid id, UpdateLevelRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteLevelAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<CheckpointViewModel>>> SearchCheckpointsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<CheckpointViewModel>> GetCheckpointAsync(Guid id, CancellationToken ct = default);
    Task<Result<CheckpointViewModel>> CreateCheckpointAsync(CreateCheckpointRequest request, CancellationToken ct = default);
    Task<Result<CheckpointViewModel>> UpdateCheckpointAsync(Guid id, UpdateCheckpointRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteCheckpointAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<DeskViewModel>>> SearchDesksAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<DeskViewModel>> GetDeskAsync(Guid id, CancellationToken ct = default);
    Task<Result<DeskViewModel>> CreateDeskAsync(CreateDeskRequest request, CancellationToken ct = default);
    Task<Result<DeskViewModel>> UpdateDeskAsync(Guid id, UpdateDeskRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteDeskAsync(Guid id, CancellationToken ct = default);
}
