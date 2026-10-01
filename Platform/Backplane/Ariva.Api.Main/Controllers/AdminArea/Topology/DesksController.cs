using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Desks (ARV-014): search, view, create, update and soft delete, limited to the caller's sites (another site's record
/// answers 404), including numbered ranges. Every change is audited.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class DesksController(ISvcTopology topology) : ControllerBase
{
    private const string Route = "api/v1/admin/desks";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchDesk))]
    [ProducesResponseType<PageViewModel<DeskViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] TopologyCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.SearchDesksAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewDesk))]
    [ProducesResponseType<DeskViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await topology.GetDeskAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateDesk))]
    [ProducesResponseType<DeskViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateDeskRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await topology.CreateDeskAsync(request, ct), view => view.Id, "/" + Route);

    /// <summary>A numbered range of desks (for example D01 to D22), at most 200, all or nothing (ARV-015).</summary>
    [HttpPost("range")]
    [Permission(nameof(Global.Defaults.Permissions.CreateDesk))]
    [ProducesResponseType<IReadOnlyList<DeskViewModel>>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRange([FromBody] CreateDeskRangeRequest request, CancellationToken ct)
    {
        var result = await topology.CreateDeskRangeAsync(request, ct);
        return result.HasErrors ? TopologyAnswers.Ok(this, result) : StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditDesk))]
    [ProducesResponseType<DeskViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDeskRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.UpdateDeskAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteDesk))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await topology.DeleteDeskAsync(id, ct));
}
