using Ariva.Core.Domain.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Validation;

/// <summary>
/// Maps validation results (ARV-104a): 404 without detail for a site, campaign or count the caller cannot see; 403 for a
/// caller the rules exclude (<see cref="ValidationErrors.Forbidden"/>: the campaign's own creator or starter); 409 for a
/// state that forbids the change (<see cref="ValidationErrors.Conflicts"/>); 400 with the rule otherwise, never the
/// request's values.
/// </summary>
internal static class ValidationAnswers
{
    /// <summary>Request bodies: a campaign (up to 250 ids and 31 days) and a count or correction.</summary>
    public const long MaxCampaignBodyBytes = 32 * 1024;

    public const long MaxCountBodyBytes = 4 * 1024;

    public static IActionResult Ok<T>(ControllerBase controller, Fluentx.Result<T> result) =>
        result.HasErrors ? Problem(controller, result.ErrorMessages) : controller.Ok(result.Data);

    public static IActionResult Created<T>(ControllerBase controller, Fluentx.Result<T> result) =>
        result.HasErrors ? Problem(controller, result.ErrorMessages) : controller.StatusCode(StatusCodes.Status201Created, result.Data);

    /// <summary>201 with the count when it was recorded now; 200 with the stored count for a resent request (same Idempotency-Key).</summary>
    public static IActionResult Captured(ControllerBase controller, Fluentx.Result<Core.Domain.ViewModels.CapturedCountViewModel> result) =>
        result.HasErrors
            ? Problem(controller, result.ErrorMessages)
            : controller.StatusCode(result.Data.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created, result.Data.Count);

    private static ObjectResult Problem(ControllerBase controller, IEnumerable<string> errors)
    {
        var first = errors?.FirstOrDefault() ?? ValidationErrors.NotFound;
        if (first == ValidationErrors.NotFound)
            return controller.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        if (ValidationErrors.Forbidden.Contains(first))
            return controller.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed", detail: first);
        return ValidationErrors.Conflicts.Contains(first)
            ? controller.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: first)
            : controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: first);
    }
}
