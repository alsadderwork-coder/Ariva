using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Airports (ARV-014): search, view, create, update and soft delete, limited to the caller's sites (another site's record
/// answers 404). Airports are deployment-wide; only an all-sites administrator changes them. Every change is audited.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class AirportsController(ISvcTopology topology) : ControllerBase
{
    private const string Route = "api/v1/admin/airports";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchAirport))]
    [ProducesResponseType<PageViewModel<AirportViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] TopologyCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.SearchAirportsAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAirport))]
    [ProducesResponseType<AirportViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await topology.GetAirportAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateAirport))]
    [ProducesResponseType<AirportViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateAirportRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await topology.CreateAirportAsync(request, ct), view => view.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditAirport))]
    [ProducesResponseType<AirportViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAirportRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await topology.UpdateAirportAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteAirport))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await topology.DeleteAirportAsync(id, ct));
}
