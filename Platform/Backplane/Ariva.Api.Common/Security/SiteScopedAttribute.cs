using Ariva.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Marks a controller or action that takes a site, airport or terminal reference (ARV-012, CWE-863). Before the action
/// runs, a <c>siteCode</c> argument outside the caller's sites answers 404, the same as a site that does not exist, so
/// the answer reveals nothing. Ids of site-bound records (airport, terminal and below) are checked by the services
/// through <see cref="ISiteScope"/>; the attribute declares that the action was written to do so, and
/// <c>SiteScopeTests</c> fails the build when such an action lacks it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SiteScopedAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Parameter and route value names that refer to a site or a site-bound parent (compared ignoring case).</summary>
    public static readonly IReadOnlySet<string> SiteReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "siteCode", "siteId", "airportId", "airportCode", "terminalId", "terminalCode"
    };

    public const string SiteCodeArgument = "siteCode";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var codes = context.ActionArguments
            .Where(argument => string.Equals(argument.Key, SiteCodeArgument, StringComparison.OrdinalIgnoreCase))
            .Select(argument => argument.Value as string)
            .ToList();
        if (codes.Count > 0)
        {
            var access = await context.HttpContext.RequestServices.GetRequiredService<ISiteScope>().GetAsync(context.HttpContext.RequestAborted);
            if (codes.Any(code => !access.Allows(code)))
            {
                context.Result = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found" })
                {
                    StatusCode = StatusCodes.Status404NotFound,
                    ContentTypes = { "application/problem+json" }
                };
                return;
            }
        }

        await next();
    }
}
