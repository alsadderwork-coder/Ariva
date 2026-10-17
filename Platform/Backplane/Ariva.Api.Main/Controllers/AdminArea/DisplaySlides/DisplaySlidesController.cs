using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.DisplaySlides;

/// <summary>
/// Instruction slides of the passenger displays (ARV-141): short multilingual plain-text slides per site, within the
/// caller's sites, audited. Seeing them needs <c>DisplayContent.View</c>; creating, changing, ordering, retiring and
/// restoring them <c>DisplayContent.Manage</c>. The players get them only through their display-authenticated board.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class DisplaySlidesController(ISvcInstructionSlides slides) : ControllerBase
{
    private const string Route = "api/v1/admin/display-slides";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewDisplayContent))]
    [ProducesResponseType<IReadOnlyList<InstructionSlideViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await slides.SearchAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewDisplayContent))]
    [ProducesResponseType<InstructionSlideViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await slides.GetAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.ManageDisplayContent))]
    [ProducesResponseType<InstructionSlideViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] InstructionSlideRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await slides.CreateAsync(request, ct), view => view.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ManageDisplayContent))]
    [ProducesResponseType<InstructionSlideViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] InstructionSlideRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await slides.UpdateAsync(id, request, ct));

    /// <summary>A retired slide is shown nowhere and kept, with its history, until it is restored.</summary>
    [HttpPost("{id:guid}/retire")]
    [Permission(nameof(Global.Defaults.Permissions.ManageDisplayContent))]
    [ProducesResponseType<InstructionSlideViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await slides.RetireAsync(id, ct));

    [HttpPost("{id:guid}/restore")]
    [Permission(nameof(Global.Defaults.Permissions.ManageDisplayContent))]
    [ProducesResponseType<InstructionSlideViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await slides.RestoreAsync(id, ct));

    /// <summary>The site's slides in a new order: the request lists every slide of the site once.</summary>
    [HttpPut("order")]
    [Permission(nameof(Global.Defaults.Permissions.ManageDisplayContent))]
    [ProducesResponseType<IReadOnlyList<InstructionSlideViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Reorder([FromQuery] string siteCode, [FromBody] InstructionSlideOrderRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await slides.ReorderAsync(siteCode, request, ct));
}
