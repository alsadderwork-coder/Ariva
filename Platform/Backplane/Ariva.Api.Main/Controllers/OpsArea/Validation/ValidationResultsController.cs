using System.Text;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.OpsArea.Validation;

/// <summary>
/// A validation campaign's results as JSON (ARV-104g; formulas F18 "Campaign verdicts"; wiki 07 section 8): each pilot criterion
/// against its target with the comparison behind it, availability, calibration records, the profile version and the geometry
/// hash, for Ariva's own campaign screen (ARV-104h); no CSV or document export (ARV-146). Reading needs <c>Validation.View</c>
/// at the campaign's site (another site, or a campaign of another site, answers 404); the answer is projected per caller from its
/// stored roles (desk-state results to border roles only, observer-level results to View or Manage holders, the shadow's figures
/// to View holders; <see cref="ValidationResultsViewModel.For"/>). A closed campaign's results are frozen as revision 1 with a
/// SHA-256 content hash (script 0050); <c>?revision=n</c> reads an earlier revision. A recomputation adds the next revision with a
/// reason: <c>Validation.Manage</c>, a second factor within 15 minutes (a critical action) and audited. Both are rate limited per
/// host (a few at once, a few waiting for a turn, 429 beyond) and answer 503 with Retry-After when the computation takes longer
/// than the request may wait.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/validation/campaigns/{id:guid}/results")]
[SiteScoped]
[EnableRateLimiting(RateLimitingExtensions.ValidationResultsPolicy)]
[RequestTimeout(ValidationResultsSettings.RequestTimeoutPolicy)]
public sealed class ValidationResultsController(ISvcValidationResults results) : ControllerBase
{
    /// <summary>A recomputation's body: a reason of at most 500 characters, with room for JSON.</summary>
    public const long MaxRecomputeBodyBytes = 4 * 1024;

    /// <summary>
    /// The campaign's results as the caller may read them: the latest frozen revision (or <paramref name="revision"/>), a closed
    /// campaign not frozen yet frozen now, a campaign not closed computed now (not stored).
    /// </summary>
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewValidation))]
    [ProducesResponseType<ValidationResultsViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(string siteCode, Guid id, [FromQuery] int? revision, CancellationToken ct) =>
        Answer(await results.GetAsync(siteCode, id, revision, ct), StatusCodes.Status200OK);

    /// <summary>
    /// Computes a closed campaign's results again and stores them as the next revision with the reason (201); the earlier revisions
    /// stay. Critical: without a second factor in the last 15 minutes the answer is 401 with <c>insufficient_user_authentication</c>.
    /// </summary>
    [HttpPost("revisions")]
    [Permission(nameof(Global.Defaults.Permissions.ManageValidation))]
    [RequiresRecentMfa]
    [RequestSizeLimit(MaxRecomputeBodyBytes)]
    [ProducesResponseType<ValidationResultsViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Recompute(string siteCode, Guid id, [FromBody] RecomputeValidationResultsRequest request, CancellationToken ct) =>
        Answer(await results.RecomputeAsync(siteCode, id, request, ct), StatusCodes.Status201Created);

    /// <summary>
    /// The JSON the results type wrote, as it is; 404 without detail for what the caller cannot see; 409 for a campaign whose state
    /// or size forbids the results; 503 with Retry-After for a computation that took too long or a busy database; 500 for a frozen
    /// document that no longer matches its hash; 400 with the rule otherwise, never the request's values.
    /// </summary>
    private IActionResult Answer(Fluentx.Result<ValidationResultsJson> answer, int status)
    {
        if (!answer.HasErrors)
            return new ContentResult { Content = Encoding.UTF8.GetString(answer.Data.Utf8.Span), ContentType = "application/json; charset=utf-8", StatusCode = status };

        var first = answer.ErrorMessages?.FirstOrDefault() ?? ValidationErrors.NotFound;
        if (first == ValidationErrors.NotFound)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        if (ValidationResultsErrors.Unavailable.Contains(first))
        {
            Response.Headers.RetryAfter = "30";
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Try again later", detail: first);
        }

        if (first == ValidationResultsErrors.Corrupt)
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Not served", detail: first);
        return ValidationResultsErrors.Conflicts.Contains(first)
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: first)
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: first);
    }
}
