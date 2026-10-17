using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Marks an advertising endpoint (ARV-145; the media library and playlists of ARV-142a and ARV-143 carry it): it acts only
/// for a site whose advertising policy allows advertising. Before the action runs, the <c>siteCode</c> argument (from the
/// route or the query) is checked: a value that cannot be a site code, a site outside the caller's sites and a site that
/// does not exist answer 404 alike (as <see cref="SiteScopedAttribute"/>); a site whose policy does not allow advertising,
/// a site without a policy and an action without a site answer 403. Zone exclusions apply to displays, not here.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AdvertisingAllowedAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var code = context.ActionArguments
            .Where(argument => string.Equals(argument.Key, SiteScopedAttribute.SiteCodeArgument, StringComparison.OrdinalIgnoreCase))
            .Select(argument => argument.Value as string)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(code))
        {
            context.Result = Answer(StatusCodes.Status403Forbidden, "Not allowed", AdvertisingPolicyErrors.Off);
            return;
        }

        var ct = context.HttpContext.RequestAborted;
        if (!Ariva.Core.Domain.Entities.Site.IsValidCode(code) ||
            !(await context.HttpContext.RequestServices.GetRequiredService<ISiteScope>().GetAsync(ct)).Allows(code))
        {
            context.Result = Answer(StatusCodes.Status404NotFound, "Not found", null);
            return;
        }

        switch (await context.HttpContext.RequestServices.GetRequiredService<ISvcAdvertisingPolicies>().AccessAsync(code, ct))
        {
            case AdvertisingAccess.Allowed:
                break;
            case AdvertisingAccess.Off:
                context.Result = Answer(StatusCodes.Status403Forbidden, "Not allowed", AdvertisingPolicyErrors.Off);
                return;
            default:
                context.Result = Answer(StatusCodes.Status404NotFound, "Not found", null);
                return;
        }

        await next();
    }

    private static ObjectResult Answer(int status, string title, string detail) =>
        new(new ProblemDetails { Status = status, Title = title, Detail = detail })
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };
}
