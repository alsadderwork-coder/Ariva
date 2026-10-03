using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Displays;

/// <summary>
/// Passenger displays (ARV-058): settings per display within the caller's sites, audited. Creating a display and issuing
/// a new credential return the player's credential once and need a second factor within the last 15 minutes.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class DisplaysController(ISvcDisplays displays) : ControllerBase
{
    private const string Route = "api/v1/admin/displays";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchDisplay))]
    [ProducesResponseType<IReadOnlyList<DisplayViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await displays.SearchAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewDisplay))]
    [ProducesResponseType<DisplayViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await displays.GetAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateDisplay))]
    [RequiresRecentMfa]
    [ProducesResponseType<DisplayIssuedViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] DisplayRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return TopologyAnswers.Created(this, await displays.CreateAsync(request, ct), view => view.Display.Id, "/" + Route);
    }

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditDisplay))]
    [ProducesResponseType<DisplayViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] DisplayRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await displays.UpdateAsync(id, request, ct));

    /// <summary>A new player credential replaces the old one at once (a lost or exposed one is replaced, never shown again).</summary>
    [HttpPost("{id:guid}/credential")]
    [Permission(nameof(Global.Defaults.Permissions.EditDisplay))]
    [RequiresRecentMfa]
    [ProducesResponseType<DisplayIssuedViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> NewCredential(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return TopologyAnswers.Ok(this, await displays.NewCredentialAsync(id, ct));
    }

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteDisplay))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await displays.DeleteAsync(id, ct));
}
