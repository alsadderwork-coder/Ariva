using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Terminals (ARV-014): search, view, create, update and soft delete, limited to the caller's sites (another site's record
/// answers 404). A terminal belongs to a site, which every record below it carries. Every change is audited.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class TerminalsController(ISvcTopology topology) : ControllerBase
{
    private const string Route = "api/v1/admin/terminals";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchTerminal))]
    [ProducesResponseType<PageViewModel<TerminalViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] TopologyCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.SearchTerminalsAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewTerminal))]
    [ProducesResponseType<TerminalViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await topology.GetTerminalAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateTerminal))]
    [ProducesResponseType<TerminalViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateTerminalRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await topology.CreateTerminalAsync(request, ct), view => view.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditTerminal))]
    [ProducesResponseType<TerminalViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTerminalRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.UpdateTerminalAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteTerminal))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await topology.DeleteTerminalAsync(id, ct));
}
