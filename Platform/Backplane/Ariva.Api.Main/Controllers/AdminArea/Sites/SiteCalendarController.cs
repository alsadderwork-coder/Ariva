using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Services.Quality;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Sites;

/// <summary>
/// A site's operating calendar (ARV-118): weekly hours in the site's local time, dated exceptions and announced maintenance
/// windows, which decide the operating minutes of the availability ledger (formulas F18). Reading needs the site's view
/// permission, changes its edit permission; every change is audited. Entries are recorded before they take effect (server
/// time) and change only until then (409 afterwards). A site the caller cannot see answers 404 like one that does not
/// exist; an entry of another site answers 404 too.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/calendar")]
[SiteScoped]
public sealed class SiteCalendarController(ISvcSiteCalendar calendar) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewSite))]
    [ProducesResponseType<SiteCalendarViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, CancellationToken ct) => Answer(await calendar.GetAsync(siteCode, ct));

    [HttpPut("weeks")]
    [Permission(nameof(Global.Defaults.Permissions.EditSite))]
    [ProducesResponseType<SiteCalendarViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetWeek(string siteCode, [FromBody] SetOperatingWeekRequest request, CancellationToken ct) =>
        Answer(await calendar.SetWeekAsync(siteCode, request, ct));

    [HttpDelete("weeks/{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditSite))]
    [ProducesResponseType<SiteCalendarViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveWeek(string siteCode, Guid id, CancellationToken ct) => Answer(await calendar.RemoveWeekAsync(siteCode, id, ct));

    [HttpPost("exceptions")]
    [Permission(nameof(Global.Defaults.Permissions.EditSite))]
    [ProducesResponseType<SiteCalendarViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddException(string siteCode, [FromBody] AddOperatingExceptionRequest request, CancellationToken ct) =>
        Answer(await calendar.AddExceptionAsync(siteCode, request, ct));

    [HttpDelete("exceptions/{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditSite))]
    [ProducesResponseType<SiteCalendarViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveException(string siteCode, Guid id, CancellationToken ct) => Answer(await calendar.RemoveExceptionAsync(siteCode, id, ct));

    [HttpPost("maintenance-windows")]
    [Permission(nameof(Global.Defaults.Permissions.EditSite))]
    [ProducesResponseType<SiteCalendarViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddMaintenance(string siteCode, [FromBody] AddMaintenanceWindowRequest request, CancellationToken ct) =>
        Answer(await calendar.AddMaintenanceAsync(siteCode, request, ct));

    [HttpDelete("maintenance-windows/{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditSite))]
    [ProducesResponseType<SiteCalendarViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelMaintenance(string siteCode, Guid id, CancellationToken ct) => Answer(await calendar.CancelMaintenanceAsync(siteCode, id, ct));

    /// <summary>404 without detail for a site or entry not found; 409 for an entry in effect or a taken day; 400 with the rule otherwise (never the request's values).</summary>
    private IActionResult Answer(Fluentx.Result<SiteCalendarViewModel> result)
    {
        if (!result.HasErrors)
            return Ok(result.Data);
        var error = result.ErrorMessages.FirstOrDefault();
        if (error is null or SiteCalendarErrors.NotFound)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        return SiteCalendarErrors.Conflicts.Contains(error)
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: error)
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: error);
    }
}
