using System.Globalization;
using Ariva.Api.Common.Filters;
using Ariva.Core.Services;
using Ariva.Core.Services.Imports;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ariva.Api.Main.Controllers.AdminArea.Uploads;

/// <summary>
/// ARV-155 and ARV-250 (CWE-400, CWE-770): every endpoint of the <c>upload</c> policy counts its caller's requests
/// (<see cref="UploadRateLimiter"/>) before the body is read, per user and <see cref="Kind"/>. The policy lets a host take one
/// upload at a time and two waiting, so without it one user starting uploads in a loop turned every other upload away. It caps
/// the transfers a user starts, not how long they hold the permit: a slow 20 MB transfer still holds it up to about 11 minutes,
/// and the policy's queue is taken before authentication, so one user's three slow transfers can hold the permit and both
/// waiting places (docs/security/costly-operations.md section 15, item 17; ARV-265). Authorization runs before resource filters, so the count is
/// the signed-in caller's own; and this filter comes before the steady upload's (<see cref="SteadyUploadAttribute"/>, order 0)
/// and the advertisement upload's own (order 0), so a refused request reads no body. A refusal answers 429 with Retry-After and a
/// fixed text, and the request log records it as a refused request (LimitReached). The count is the caller's user id (an Ariva
/// access token always carries it, and a principal without one gets no stored grants); only the test scheme's principals, which
/// have none, count by their name. A caller with neither is refused 403 rather than counted under no one.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class UploadLimitAttribute(string kind) : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    #region Kinds

    /// <summary>A CSV import's preview (devices and desk code mappings: one allowance for both, ARV-155).</summary>
    public const string CsvPreview = "csv-preview";

    /// <summary>A CSV import's commit, counted apart from the previews.</summary>
    public const string CsvCommit = "csv-commit";

    /// <summary>A floor plan's upload.</summary>
    public const string FloorPlan = "floor-plan";

    /// <summary>An SSIM file's preview.</summary>
    public const string FlightSchedulePreview = "flight-schedule-preview";

    /// <summary>An SSIM file's import, counted apart from the previews.</summary>
    public const string FlightScheduleImport = "flight-schedule-import";

    /// <summary>A site configuration's export (it shares the upload policy and builds a bundle of up to 20 MB).</summary>
    public const string SiteConfigurationExport = "site-configuration-export";

    /// <summary>A site configuration bundle's import.</summary>
    public const string SiteConfigurationImport = "site-configuration-import";

    /// <summary>An advertisement image or video.</summary>
    public const string AdvertisingMedia = "advertising-media";

    #endregion

    /// <summary>The refusal of every kind but the CSV imports' (theirs is <see cref="CsvImportErrors.TooManyImports"/>).</summary>
    public const string TooManyUploads = "Too many uploads or transfers of this kind for now: try again after the time Retry-After gives.";

    #region Properties

    /// <summary>The kind counted: one of the constants above.</summary>
    public string Kind { get; } = kind;

    /// <summary>Before the steady upload's resource step and the advertisement upload's, which read the body.</summary>
    public int Order => -1;

    #endregion

    #region Methods

    /// <summary>Whether a kind is a CSV import's (counted in <c>Security:RateLimiting:CsvImports</c>).</summary>
    /// <param name="kind">The kind.</param>
    public static bool IsCsvImport(string kind) => kind is CsvPreview or CsvCommit;

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

        var (allowed, retryAfter) = services.GetRequiredService<UploadRateLimiter>().TryAcquire(caller, Kind);
        if (!allowed)
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            context.Result = SteadyUploadAttribute.Answer(StatusCodes.Status429TooManyRequests, "Too many requests",
                IsCsvImport(Kind) ? CsvImportErrors.TooManyImports : TooManyUploads);
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

    #endregion
}
