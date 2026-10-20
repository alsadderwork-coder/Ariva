using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.WebUtilities;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// ARV-096 (CWE-400, CWE-770, ASVS 5.0 V15.2.2): an upload arrives steadily, or it ends. The <c>upload</c> concurrency policy lets a
/// host take one upload at a time (two more wait), so a sender that trickles its body would otherwise hold that one slot for as long
/// as Kestrel's default minimum rate allows (240 bytes a second: a day for 20 MB), blocking every other upload and the site
/// configuration export. Every endpoint under that policy carries this filter, or the advertisement upload's, which takes the same
/// steps (<c>SteadyUploadTests</c> keeps the inventory). Before model binding it:
/// <list type="bullet">
/// <item>sets the request's minimum body data rate to <see cref="MinBytesPerSecond"/> after <see cref="GraceSeconds"/>, over HTTP/1.x
/// only (Kestrel refuses a rate per request on HTTP/2 and HTTP/3, whose connections carry the server's own). At 32 KB a second the
/// largest body these endpoints take arrives in time: a 20 MB floor plan or SSIM file with the form's framing in about 11 minutes
/// (21,037,056 bytes at 32,768 a second is 642 s; a 20 MB site configuration bundle 640 s), within the 15 minute <c>transfer</c>
/// request timeout, and a 1 MB CSV in about 32 seconds;</item>
/// <item>reads the body itself with the request's token, since MVC's form reader and JSON input formatter read without one, so the
/// <c>transfer</c> timeout (which cancels that token) ends the read on every protocol: a form with <c>ReadFormAsync</c>, whatever the
/// action binds (MVC's form value providers read a form sent to any action with parameters anyway, without the token; model binding
/// then takes the form already read), and a body that is not a form, for an action that binds it whole (<c>[FromBody]</c>), buffered
/// (in memory, then a temporary file, within the endpoint's size limit) for model binding to read again; any other body is never
/// read here;</item>
/// <item>answers with fixed texts, never what was sent: a body larger than the endpoint takes (declared, or growing past it while
/// read) 413 with <see cref="TooLarge"/>, a body too slow 408 and a cut short or malformed one 400 with <see cref="Unreadable"/>.</item>
/// </list>
/// It runs after the request size and form limits' own filters (authorization filters run before resource filters), so the read keeps
/// the endpoint's limits, and after authentication and authorization (endpoint routing), so a caller without the permission is
/// answered before anything is read. A request without a body (the export) is left as it is, apart from the rate.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SteadyUploadAttribute : Attribute, IAsyncResourceFilter
{
    #region Constants

    /// <summary>The slowest an upload may arrive over HTTP/1.x: 32 KB a second, averaged once <see cref="GraceSeconds"/> have passed.</summary>
    public const double MinBytesPerSecond = 32 * 1024;

    /// <summary>The seconds an upload has before <see cref="MinBytesPerSecond"/> applies.</summary>
    public const int GraceSeconds = 10;

    /// <summary>The 408 and 400 answers' text: a body that could not be read whole (too slow, cut short or malformed).</summary>
    public const string Unreadable = "The upload could not be read: a body within the endpoint's limits, arriving steadily.";

    /// <summary>The 413 answer's text for an endpoint that names none of its own.</summary>
    public const string TooLargeByDefault = "The upload is larger than this endpoint takes.";

    /// <summary>The bytes of a buffered body kept in memory before the rest goes to a temporary file (ASP.NET Core's own default).</summary>
    private const int MemoryBufferBytes = 30 * 1024;

    #endregion

    #region Properties

    /// <summary>The minimum rate, as Kestrel takes it.</summary>
    public static MinDataRate MinimumRate { get; } = new(MinBytesPerSecond, TimeSpan.FromSeconds(GraceSeconds));

    /// <summary>The 413 answer's fixed text: the endpoint's own refusal of a file too large (a constant).</summary>
    public string TooLarge { get; set; } = TooLargeByDefault;

    #endregion

    #region IAsyncResourceFilter

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        RequireSteadyBody(http);

        IActionResult refused = null;
        if (http.Request.HasFormContentType)
            refused = await ReadFormAsync(http, TooLargeAnswer, Unreadable);
        else if (Binds(context.ActionDescriptor, BindingSource.Body))
            refused = await BufferBodyAsync(http, TooLargeAnswer, Unreadable);
        if (refused is not null)
        {
            context.Result = refused;
            return;
        }

        await next();
    }

    #endregion

    #region Steps (the advertisement upload's filter takes them too)

    /// <summary>
    /// Sets <see cref="MinimumRate"/> on the request, over HTTP/1.x only: Kestrel takes a per-request rate there and refuses one
    /// (throws) on HTTP/2 and HTTP/3. Call it before anything reads the body: Kestrel takes the rate when the read starts.
    /// </summary>
    /// <param name="http">The request's context.</param>
    public static void RequireSteadyBody(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if ((HttpProtocol.IsHttp11(http.Request.Protocol) || HttpProtocol.IsHttp10(http.Request.Protocol)) &&
            http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
            rate.MinDataRate = MinimumRate;
    }

    /// <summary>
    /// Reads the form with the request's token, within the limits already set (the request size and the form limits). Null when it
    /// was read; otherwise the answer: <paramref name="tooLarge"/> for a body past the size limit (413), <paramref name="unreadable"/>
    /// with 408 for a body too slow (or Kestrel's other refusals with their own status), and with 400 for a malformed or cut short
    /// form, or one past the form limits, as model binding answers it. A request cancelled (the client gone, or the request timeout)
    /// is not answered here: the request timeout's middleware answers its own cancellation (503).
    /// </summary>
    /// <param name="http">The request's context.</param>
    /// <param name="tooLarge">The endpoint's 413 answer.</param>
    /// <param name="unreadable">The endpoint's text for a form that could not be read.</param>
    /// <returns>Null, or the answer.</returns>
    public static async Task<IActionResult> ReadFormAsync(HttpContext http, Func<IActionResult> tooLarge, string unreadable)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tooLarge);
        try
        {
            await http.Request.ReadFormAsync(http.RequestAborted);
            return null;
        }
        catch (BadHttpRequestException e)
        {
            return Refusal(e, tooLarge, unreadable);
        }
        catch (Exception e) when (e is InvalidDataException || (e is IOException && !http.RequestAborted.IsCancellationRequested))
        {
            return Answer(StatusCodes.Status400BadRequest, "Not valid", unreadable);
        }
    }

    /// <summary>
    /// Reads a body that is not a form whole with the request's token and keeps it (in memory up to 30 KB, then in a temporary file
    /// removed with the request), rewound, for model binding to read again. The answers are those of <see cref="ReadFormAsync"/>.
    /// </summary>
    /// <param name="http">The request's context.</param>
    /// <param name="tooLarge">The endpoint's 413 answer.</param>
    /// <param name="unreadable">The endpoint's text for a body that could not be read.</param>
    /// <returns>Null, or the answer.</returns>
    public static async Task<IActionResult> BufferBodyAsync(HttpContext http, Func<IActionResult> tooLarge, string unreadable)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tooLarge);
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
        if (http.Request.ContentLength > limit)
            return tooLarge();

        if (limit is { } most)
            http.Request.EnableBuffering(MemoryBufferBytes, most);
        else
            http.Request.EnableBuffering(MemoryBufferBytes);
        try
        {
            await http.Request.Body.DrainAsync(http.RequestAborted);
            http.Request.Body.Position = 0;
            return null;
        }
        catch (BadHttpRequestException e)
        {
            return Refusal(e, tooLarge, unreadable);
        }
        catch (IOException) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Answer(StatusCodes.Status400BadRequest, "Not valid", unreadable);
        }
    }

    /// <summary>A ProblemDetails answer of fixed texts.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="title">The title.</param>
    /// <param name="detail">The detail.</param>
    /// <returns>The answer, as <c>application/problem+json</c>.</returns>
    public static ObjectResult Answer(int status, string title, string detail) =>
        new(new ProblemDetails { Status = status, Title = title, Detail = detail })
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };

    #endregion

    #region Helpers

    private IActionResult TooLargeAnswer() => Answer(StatusCodes.Status413PayloadTooLarge, "Too large", TooLarge);

    /// <summary>Kestrel's refusal of the body: too large (413), too slow (408) or another, answered with fixed text.</summary>
    private static IActionResult Refusal(BadHttpRequestException e, Func<IActionResult> tooLarge, string unreadable) =>
        e.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? tooLarge()
            : Answer(e.StatusCode, "Not read", unreadable);

    /// <summary>True when the action binds a parameter from one of these sources, as declared or inferred for <c>[ApiController]</c> (here the whole body, <c>[FromBody]</c>).</summary>
    private static bool Binds(ActionDescriptor action, params BindingSource[] sources) =>
        action?.Parameters.Any(p => p.BindingInfo?.BindingSource is { } source && sources.Contains(source)) == true;

    #endregion
}
