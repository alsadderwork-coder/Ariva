using System.Globalization;
using Ariva.Api.Common.Filters;
using Ariva.Core.Services;
using Ariva.Core.Services.Imports;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ariva.Api.Main.Controllers.AdminArea.Imports;

/// <summary>
/// ARV-155 (CWE-400, CWE-770): a user's CSV import previews, and apart from them their commits, are counted
/// (<see cref="CsvImportRateLimiter"/>) before the file is read. Authorization runs before resource filters, so the count is
/// the signed-in caller's own; and this filter comes before <see cref="SteadyUploadAttribute"/> (its order is -1, the upload
/// filter's 0), so a refused request reads no body. A refusal answers 429 with Retry-After and a fixed text, and the request
/// log records it as a refused request (LimitReached). The count is the caller's user id (an Ariva access token always carries
/// it, and a principal without one gets no stored grants); only the test scheme's principals, which have none, count by their
/// name. A caller with neither is refused 403 rather than counted under no one.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class CsvImportLimitAttribute(string step) : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    /// <summary>The step of an import preview.</summary>
    public const string Preview = "preview";

    /// <summary>The step of an import commit.</summary>
    public const string Commit = "commit";

    /// <summary>The step counted: <see cref="Preview"/> or <see cref="Commit"/>.</summary>
    public string Step { get; } = step;

    /// <summary>Before the upload filter's resource step, which reads the body.</summary>
    public int Order => -1;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var services = context.HttpContext.RequestServices;
        if (CallerOf(services.GetRequiredService<ICurrentUser>()) is not { } caller)
        {
            context.Result = SteadyUploadAttribute.Answer(StatusCodes.Status403Forbidden, "Forbidden", null);
            return;
        }

        var (allowed, retryAfter) = services.GetRequiredService<CsvImportRateLimiter>().TryAcquire(caller, Step);
        if (!allowed)
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            context.Result = SteadyUploadAttribute.Answer(StatusCodes.Status429TooManyRequests, "Too many requests", CsvImportErrors.TooManyImports);
            return;
        }

        await next();
    }

    /// <summary>The key a caller is counted under: its user id, else its name; null for neither.</summary>
    /// <param name="user">The request's caller.</param>
    public static string CallerOf(ICurrentUser user) =>
        user?.Id is { } id ? "id:" + id.ToString("N", CultureInfo.InvariantCulture)
        : string.IsNullOrEmpty(user?.UserName) ? null
        : "name:" + user.UserName;
}
