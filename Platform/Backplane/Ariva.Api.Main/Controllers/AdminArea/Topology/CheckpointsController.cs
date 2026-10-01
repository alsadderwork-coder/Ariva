using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Checkpoints (ARV-014): search, view, create, update and soft delete, limited to the caller's sites (another site's record
/// answers 404). Every change is audited.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class CheckpointsController(ISvcTopology topology) : ControllerBase
{
    private const string Route = "api/v1/admin/checkpoints";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchCheckpoint))]
    [ProducesResponseType<PageViewModel<CheckpointViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] TopologyCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.SearchCheckpointsAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewCheckpoint))]
    [ProducesResponseType<CheckpointViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await topology.GetCheckpointAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateCheckpoint))]
    [ProducesResponseType<CheckpointViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateCheckpointRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await topology.CreateCheckpointAsync(request, ct), view => view.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditCheckpoint))]
    [ProducesResponseType<CheckpointViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCheckpointRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.UpdateCheckpointAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteCheckpoint))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await topology.DeleteCheckpointAsync(id, ct));
}
