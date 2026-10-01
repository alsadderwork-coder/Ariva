using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Desk code mappings (ARV-015): AMAN desk and gate codes and AODB counter codes mapped to Ariva desks, site-scoped and
/// audited.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class DeskCodeMappingsController(ISvcDeskCodeMappings mappings) : ControllerBase
{
    private const string Route = "api/v1/admin/desk-code-mappings";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchDeskCodeMapping))]
    [ProducesResponseType<PageViewModel<DeskCodeMappingViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] DeskCodeMappingCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await mappings.SearchAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewDeskCodeMapping))]
    [ProducesResponseType<DeskCodeMappingViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await mappings.GetAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateDeskCodeMapping))]
    [ProducesResponseType<DeskCodeMappingViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateDeskCodeMappingRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await mappings.CreateAsync(request, ct), view => view.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditDeskCodeMapping))]
    [ProducesResponseType<DeskCodeMappingViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDeskCodeMappingRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await mappings.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteDeskCodeMapping))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await mappings.DeleteAsync(id, ct));
}
