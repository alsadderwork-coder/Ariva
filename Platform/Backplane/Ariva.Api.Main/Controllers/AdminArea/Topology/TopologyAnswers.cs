using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>Maps topology results: 404 not found, 409 duplicate or in use, 403 deployment-wide, 400 otherwise.</summary>
internal static class TopologyAnswers
{
    public static IActionResult Ok<T>(ControllerBase controller, Fluentx.Result<T> result) =>
        result.HasErrors ? Problem(controller, result.ErrorMessages) : controller.Ok(result.Data);

    public static IActionResult Created<T>(ControllerBase controller, Fluentx.Result<T> result, Func<T, Guid> id, string route) =>
        result.HasErrors ? Problem(controller, result.ErrorMessages) : controller.Created($"{route}/{id(result.Data)}", result.Data);

    public static IActionResult NoContent(ControllerBase controller, Fluentx.Result<bool> result) =>
        result.HasErrors ? Problem(controller, result.ErrorMessages) : controller.NoContent();

    private static ObjectResult Problem(ControllerBase controller, IEnumerable<string> errors)
    {
        var first = errors?.FirstOrDefault() ?? TopologyErrors.NotFound;
        var (status, title) = first switch
        {
            TopologyErrors.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            TopologyErrors.DeploymentWide => (StatusCodes.Status403Forbidden, "Not allowed"),
            _ when TopologyErrors.Conflicts.Contains(first) => (StatusCodes.Status409Conflict, "Conflict"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        return controller.Problem(statusCode: status, title: title, detail: status == StatusCodes.Status404NotFound ? null : first);
    }
}
