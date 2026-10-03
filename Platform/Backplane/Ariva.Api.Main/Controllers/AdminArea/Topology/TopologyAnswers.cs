using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Maps topology and device results: 404 not found, 409 duplicate, in use, not a draft or a retired device, 403 deployment-wide, 400 otherwise. When
/// there are several errors (a draft's publishing problems) all of them go in <c>problems</c>.
/// </summary>
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
            _ when TopologyErrors.Conflicts.Contains(first) || ZoneProfileErrors.Conflicts.Contains(first) || Ariva.Core.Services.Sensing.DeviceErrors.Conflicts.Contains(first) ||
                   Ariva.Core.Services.Alerting.AlertErrors.Conflicts.Contains(first) || Ariva.Core.Services.Displays.DisplayErrors.Conflicts.Contains(first) => (StatusCodes.Status409Conflict, "Conflict"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        var problem = controller.Problem(statusCode: status, title: title, detail: status == StatusCodes.Status404NotFound ? null : first);
        var all = errors?.ToList() ?? [];
        if (all.Count > 1 && status != StatusCodes.Status404NotFound && problem.Value is ProblemDetails details)
            details.Extensions["problems"] = all.Skip(1).ToList();
        return problem;
    }
}
