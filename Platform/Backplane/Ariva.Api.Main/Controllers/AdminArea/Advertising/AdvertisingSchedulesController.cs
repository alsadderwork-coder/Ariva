using System.ComponentModel.DataAnnotations;
using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Advertising;

/// <summary>
/// A site's advertising schedules and a display's timeline (ARV-143a, wiki 11 section 9): a schedule plays a playlist of the
/// site on displays of the site (all of them, those showing given queue zones, or named ones) on days of the week within a
/// local time window; the timeline shows what one display plays over the week. Every action acts only while the site's
/// advertising policy allows advertising (<see cref="AdvertisingAllowedAttribute"/>); a schedule of another site answers
/// 404 under this site's route. Every change is audited; a schedule is retired, never deleted.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/advertising")]
[SiteScoped]
[AdvertisingAllowed]
public sealed class AdvertisingSchedulesController(ISvcAdvertisingSchedules schedules) : ControllerBase
{
    [HttpGet("schedules")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<IReadOnlyList<AdvertisingScheduleViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await schedules.ListAsync(siteCode, ct));

    [HttpGet("schedules/{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingScheduleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await schedules.GetAsync(siteCode, id, ct));

    [HttpPost("schedules")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingScheduleViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(string siteCode, [FromBody] AdvertisingScheduleRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await schedules.CreateAsync(siteCode, request, ct), s => s.Id, $"/api/v1/admin/sites/{siteCode}/advertising/schedules");

    [HttpPut("schedules/{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingScheduleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string siteCode, Guid id, [FromBody] AdvertisingScheduleRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await schedules.UpdateAsync(siteCode, id, request, ct));

    [HttpPost("schedules/{id:guid}/retire")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingScheduleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retire(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await schedules.RetireAsync(siteCode, id, ct));

    [HttpPost("schedules/{id:guid}/restore")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingScheduleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Restore(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await schedules.RestoreAsync(siteCode, id, ct));

    /// <summary>What one display of the site plays over the week from the site's local midnight today (no display named: 404).</summary>
    [HttpGet("timeline")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingTimelineViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Timeline(string siteCode, [FromQuery, MaxLength(40)] string display, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await schedules.TimelineAsync(siteCode, display, ct));
}
