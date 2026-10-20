using Ariva.Api.Common.Filters;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Services.Displays;
using Ariva.Infra.Services.Displays;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Main.Controllers.AdminArea.Advertising;

/// <summary>
/// The advertisement upload's body limit for this installation (ARV-142b, CWE-400): the endpoint declares the ceiling every
/// installation keeps (a 100 MB video and the form's framing), and before the form is read this lowers it to what this
/// installation takes, the larger of an image's 10 MB and <c>Advertising:Videos:MaxMegabytes</c>, plus the framing. A body
/// declared larger answers 413 at once; the form is then read here, so a body sent without a length (chunked) that grows
/// past the limit is cut off at it and answers the same 413, and nothing larger than the installation takes is buffered.
/// The rate and the read are the steady upload's (<see cref="SteadyUploadAttribute"/>, ARV-096), which every other endpoint of the
/// <c>upload</c> policy carries: over HTTP/1.x, a body trickling in slower than <see cref="MinBytesPerSecond"/> (after a grace
/// period) is cut off (408), so a slow sender cannot hold the one upload a host takes at a time for hours; the form is read with the
/// request's token, so the <c>transfer</c> request timeout (15 minutes by default) ends it too. It runs after the request size and
/// form limits' own filters (authorization filters), which it lowers.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AdvertisingUploadLimitAttribute : Attribute, IAsyncResourceFilter
{
    /// <summary>What a form adds to the file: its boundaries, headers and the details' fields.</summary>
    public const long Framing = 64 * 1024;

    /// <summary>The slowest an upload may arrive: 32 KB a second (a 20 MB video in at most 11 minutes), after 10 seconds' grace (the steady upload's).</summary>
    public const double MinBytesPerSecond = SteadyUploadAttribute.MinBytesPerSecond;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var settings = http.RequestServices.GetRequiredService<IOptions<AdvertisingVideoSettings>>().Value;
        var largest = Math.Max(AdvertisingMedia.MaxImageBytes, settings.MaxBytes);
        var limit = largest + Framing;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size && (size.MaxRequestBodySize ?? long.MaxValue) > limit)
            size.MaxRequestBodySize = limit;
        SteadyUploadAttribute.RequireSteadyBody(http);
        if (http.Request.ContentLength > limit)
        {
            context.Result = TooLarge(largest);
            return;
        }

        // Read here, within the limits set above; model binding then takes the form already read. Too large (413), too slow (408),
        // malformed, cut short or past the form limits (16 fields of at most 1 KB: 400): each answered with fixed text.
        if (http.Request.HasFormContentType && await SteadyUploadAttribute.ReadFormAsync(http, () => TooLarge(largest), UnreadableForm) is { } refused)
        {
            context.Result = refused;
            return;
        }

        await next();
    }

    /// <summary>The answer to a form that could not be read whole.</summary>
    public const string UnreadableForm = "The upload could not be read: a form with one file and the item's details, within the limits, arriving steadily.";

    private static ObjectResult TooLarge(long largest) =>
        SteadyUploadAttribute.Answer(StatusCodes.Status413PayloadTooLarge, "Too large", AdvertisingMediaErrors.TooLargeFor(largest));
}
