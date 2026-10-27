using System.Globalization;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Logging;
using Ariva.Core.Services;
using Ariva.Core.Services.Imports;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ariva.Api.Main.Controllers.AdminArea.Uploads;

/// <summary>
/// ARV-155, ARV-250 and ARV-265 (CWE-400, CWE-770): every endpoint of the <c>upload</c> policy takes its place on the host and
/// counts its caller's requests (<see cref="UploadRateLimiter"/>) before the body is read. Authorization runs before resource
/// filters, so the caller is the signed-in user; and this filter comes before the steady upload's
/// (<see cref="SteadyUploadAttribute"/>, order 0) and the advertisement upload's own (order 0), so a refused request reads no body.
/// In order:
/// <list type="number">
/// <item>The caller's window for the <see cref="Kind"/> (ARV-250): a caller past it is refused at once, before taking a place.</item>
/// <item>The caller's share of the host's places (ARV-265): while one of their uploads or transfers runs or waits on this host
/// process, the next is refused at once (<see cref="UploadUnderway"/>), so one user's slow transfers hold one place, not the
/// permit and both waiting places.</item>
/// <item>A place on the host: the permit, or a waiting place oldest first; with every waiting place taken, refused at once
/// (<see cref="UploadsBusy"/>). The wait is within the request's <c>transfer</c> timeout. The rate limiting middleware took this
/// place before ARV-265, ahead of authentication, which is how one user could hold them all.</item>
/// <item>The count in the caller's window, taken once the request holds the permit, so a request the host turns away spends none
/// of the caller's allowance.</item>
/// </list>
/// The place and the share are held until the request ends. Each refusal answers 429 with Retry-After and a fixed text: the
/// window's in the request log (LimitReached, 9129), the share's and the host's as a rate limiting rejection of the <c>upload</c>
/// policy (9116, partition <c>user</c> or <c>host</c>). The caller is counted by their user id (an Ariva access token always carries
/// it, and a principal without one gets no stored grants); only the test scheme's principals, which have none, count by their
/// name. A caller with neither is refused 403 rather than counted under no one.
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

    /// <summary>The refusal of a user's upload or transfer while another of theirs runs or waits on the host (ARV-265).</summary>
    public const string UploadUnderway = "Another upload or transfer of yours is still running or waiting on this server: try again once it has ended.";

    /// <summary>The refusal of an upload or transfer while the host's permit and every waiting place are taken (ARV-265).</summary>
    public const string UploadsBusy = "The server is taking as many uploads and transfers as it can: try again after the time Retry-After gives.";

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
        var http = context.HttpContext;
        var services = http.RequestServices;
        if (CallerOf(services.GetRequiredService<ICurrentUser>()) is not { } caller)
        {
            context.Result = SteadyUploadAttribute.Answer(StatusCodes.Status403Forbidden, "Forbidden", null);
            return;
        }

        var limiter = services.GetRequiredService<UploadRateLimiter>();
        if (limiter.Exhausted(caller, Kind) is { } wait)
        {
            TooManyOfKind(context, wait);
            return;
        }

        using var share = limiter.TakeShare(caller);
        if (!share.IsAcquired)
        {
            Refuse(context, UploadUnderway, SecurityEvents.UserPartition);
            return;
        }

        using var place = await limiter.TakePlaceAsync(http.RequestAborted);
        if (!place.IsAcquired)
        {
            Refuse(context, UploadsBusy, SecurityEvents.HostPartition);
            return;
        }

        var (allowed, retryAfter) = limiter.TryAcquire(caller, Kind);
        if (!allowed)
        {
            TooManyOfKind(context, retryAfter);
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

    #region Helpers

    /// <summary>The caller's window for the kind is used up: 429 with the window's Retry-After and the kind's fixed text.</summary>
    private void TooManyOfKind(ResourceExecutingContext context, TimeSpan retryAfter)
    {
        context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        context.Result = SteadyUploadAttribute.Answer(StatusCodes.Status429TooManyRequests, "Too many requests",
            IsCsvImport(Kind) ? CsvImportErrors.TooManyImports : TooManyUploads);
    }

    /// <summary>
    /// A place refused (ARV-265): 429 with the concurrency policies' fixed Retry-After, since a place frees when a request ends, and
    /// the rate limiting rejection the middleware logged for this policy before, with the kind of partition only.
    /// </summary>
    private static void Refuse(ResourceExecutingContext context, string text, string partition)
    {
        var http = context.HttpContext;
        http.Response.Headers.RetryAfter = RateLimitingExtensions.ConcurrencyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        http.RequestServices.GetService<SecurityEventLog>()?.RateLimited(http, RateLimitingExtensions.UploadPolicy, _ => partition);
        context.Result = SteadyUploadAttribute.Answer(StatusCodes.Status429TooManyRequests, "Too many requests", text);
    }

    #endregion
}
