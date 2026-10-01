using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Levels (ARV-014): search, view, create, update and soft delete, limited to the caller's sites (another site's record
/// answers 404). Every change is audited.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class LevelsController(ISvcTopology topology) : ControllerBase
{
    private const string Route = "api/v1/admin/levels";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchLevel))]
    [ProducesResponseType<PageViewModel<LevelViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] TopologyCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.SearchLevelsAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewLevel))]
    [ProducesResponseType<LevelViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await topology.GetLevelAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateLevel))]
    [ProducesResponseType<LevelViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateLevelRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await topology.CreateLevelAsync(request, ct), view => view.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditLevel))]
    [ProducesResponseType<LevelViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLevelRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.UpdateLevelAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteLevel))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await topology.DeleteLevelAsync(id, ct));
}
