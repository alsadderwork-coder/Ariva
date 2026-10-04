using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Services.Reports;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Reports;

/// <summary>
/// Report schedules (ARV-060): the daily report of a site of the caller's, emailed at a local time to Ariva accounts that
/// may read it. Changes are audited; deliveries run in Ariva.Api.Cronz.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class ReportSchedulesController(ISvcReportSchedules schedules) : ControllerBase
{
    private const string Route = "api/v1/report-schedules";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchReportSchedule))]
    [ProducesResponseType<IReadOnlyList<ReportScheduleViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string siteCode, CancellationToken ct) => Answer(await schedules.SearchAsync(siteCode, ct));

    /// <summary>The accounts a schedule of the site may send to.</summary>
    [HttpGet("recipients")]
    [Permission(nameof(Global.Defaults.Permissions.CreateReportSchedule), nameof(Global.Defaults.Permissions.EditReportSchedule))]
    [ProducesResponseType<IReadOnlyList<ReportRecipientViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Recipients([FromQuery] string siteCode, CancellationToken ct) => Answer(await schedules.RecipientsAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewReportSchedule))]
    [ProducesResponseType<ReportScheduleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Answer(await schedules.GetAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateReportSchedule))]
    [ProducesResponseType<ReportScheduleViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] ReportScheduleRequest request, CancellationToken ct)
    {
        var result = await schedules.CreateAsync(request, ct);
        return result.HasErrors ? ReportAnswers.Problem(this, result.ErrorMessages.FirstOrDefault()) : Created($"/{Route}/{result.Data.Id}", result.Data);
    }

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditReportSchedule))]
    [ProducesResponseType<ReportScheduleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ReportScheduleRequest request, CancellationToken ct) => Answer(await schedules.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteReportSchedule))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await schedules.DeleteAsync(id, ct);
        return result.HasErrors ? ReportAnswers.Problem(this, result.ErrorMessages.FirstOrDefault()) : NoContent();
    }

    private IActionResult Answer<T>(Fluentx.Result<T> result) => result.HasErrors ? ReportAnswers.Problem(this, result.ErrorMessages.FirstOrDefault()) : Ok(result.Data);
}
