using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Validation;

/// <summary>
/// Timed tracers of a validation campaign (ARV-104b; formulas F18, F19). The observer's side, under <c>Validation.Capture</c>:
/// a batch of runs from the capturing device with the device's own clock reading (<c>deviceClockUtc</c>, read again at every
/// retry), from which the server measures the offset and corrects every run (refused beyond 5 minutes); the
/// <c>Idempotency-Key</c> header is required and belongs to its observer (a resend returns the stored batch, 200; the key with
/// another batch is 409); and the caller's own runs. A manager (<c>Validation.View</c>) reads every observer's runs. A site
/// the caller cannot see and a campaign of another site answer 404; the campaign's creator or starter is refused (403); a
/// campaign that is not running answers 409; times outside the planned days or in the future answer 400 without repeating them.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/validation")]
[SiteScoped]
public sealed class TracerRunsController(ISvcTracerRuns runs) : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Every observer's runs of the campaign: by zone, observer, tracer code and join range (UTC), sorted from an allowlist.</summary>
    [HttpGet("campaigns/{id:guid}/tracer-runs")]
    [Permission(nameof(Global.Defaults.Permissions.ViewValidation))]
    [ProducesResponseType<PageViewModel<TracerRunViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(string siteCode, Guid id, [FromQuery] TracerRunCriteria criteria, CancellationToken ct) =>
        ValidationAnswers.Ok(this, await runs.SearchAsync(siteCode, id, criteria, ct));

    /// <summary>The caller's own runs of the campaign.</summary>
    [HttpGet("capture/campaigns/{id:guid}/tracer-runs")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [ProducesResponseType<PageViewModel<TracerRunViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OwnRuns(string siteCode, Guid id, [FromQuery] TracerRunCriteria criteria, CancellationToken ct) =>
        ValidationAnswers.Ok(this, await runs.OwnRunsAsync(siteCode, id, criteria, ct));

    /// <summary>Records a batch of 1 to 20 runs (201), or returns the stored batch for a resend with the same Idempotency-Key (200).</summary>
    [HttpPost("capture/campaigns/{id:guid}/tracer-runs")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [RequestSizeLimit(ValidationAnswers.MaxBatchBodyBytes)]
    [ProducesResponseType<TracerBatchViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<TracerBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Capture(string siteCode, Guid id, [FromBody] CaptureTracerRunsRequest request,
        [FromHeader(Name = IdempotencyHeader)] string idempotencyKey, CancellationToken ct) =>
        ValidationAnswers.Recorded(this, await runs.CaptureAsync(siteCode, id, request, idempotencyKey, ct), r => r.Replayed, r => r.Batch);
}
