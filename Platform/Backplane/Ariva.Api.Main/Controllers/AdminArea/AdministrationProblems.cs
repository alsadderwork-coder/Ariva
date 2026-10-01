using Ariva.Core.Services.Administration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea;

/// <summary>Maps administration errors to problem responses: 404 not found, 403 not allowed, 409 taken, 400 otherwise.</summary>
internal static class AdministrationProblems
{
    public static ObjectResult For(ControllerBase controller, IEnumerable<string> errors)
    {
        var first = errors?.FirstOrDefault() ?? AdministrationErrors.NotFound;
        var (status, title) = first switch
        {
            AdministrationErrors.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            AdministrationErrors.UserNameTaken or AdministrationErrors.SiteTaken => (StatusCodes.Status409Conflict, "Already exists"),
            _ when AdministrationErrors.Forbidden.Contains(first) => (StatusCodes.Status403Forbidden, "Not allowed"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        return controller.Problem(statusCode: status, title: title, detail: status == StatusCodes.Status404NotFound ? null : first);
    }
}
