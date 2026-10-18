using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Advertising;

/// <summary>
/// A site's advertising playlists (ARV-143a, wiki 11 section 9): ordered advertisements of the site's library and instruction
/// slides of the site, each with its time on screen and weight. Every action acts only while the site's advertising policy
/// allows advertising (<see cref="AdvertisingAllowedAttribute"/>: 403 while it is off, 404 alike for a site outside the
/// caller's sites and one that does not exist); a playlist of another site answers 404 under this site's route. Every change
/// is audited; a playlist is retired, never deleted.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/advertising/playlists")]
[SiteScoped]
[AdvertisingAllowed]
public sealed class AdvertisingPlaylistsController(ISvcAdvertisingPlaylists playlists) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<IReadOnlyList<AdvertisingPlaylistViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await playlists.ListAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingPlaylistViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await playlists.GetAsync(siteCode, id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingPlaylistViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(string siteCode, [FromBody] AdvertisingPlaylistRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await playlists.CreateAsync(siteCode, request, ct), p => p.Id, $"/api/v1/admin/sites/{siteCode}/advertising/playlists");

    /// <summary>Replaces the playlist's name and items (the whole list each time).</summary>
    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingPlaylistViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string siteCode, Guid id, [FromBody] AdvertisingPlaylistRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await playlists.UpdateAsync(siteCode, id, request, ct));

    /// <summary>Retires the playlist: it is kept, and its schedules stop applying until it is restored.</summary>
    [HttpPost("{id:guid}/retire")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingPlaylistViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retire(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await playlists.RetireAsync(siteCode, id, ct));

    [HttpPost("{id:guid}/restore")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingPlaylistViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Restore(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await playlists.RestoreAsync(siteCode, id, ct));
}
