using System.Globalization;
using Ariva.Infra.Resilience;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// ARV-072: a request that failed because PostgreSQL or Redis is unreachable or stalled (<see cref="DependencyOutage"/>)
/// answers 503 with Retry-After rather than 500, so a sensor keeps its batch and pushes it again, a display keeps its
/// last board until it goes stale, and the web app retries. The body is the usual ProblemDetails, without details
/// outside vm-local. Logged as a warning with the exception: the outage, not a fault in the code, but still traceable.
/// </summary>
public sealed class DependencyOutageHandler(IProblemDetailsService problemDetails, ILogger<DependencyOutageHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is null || !DependencyOutage.Is(exception) || httpContext.Response.HasStarted)
            return false;

        // With the exception (through the redaction enricher), since the middleware logs nothing once a handler took it:
        // a timeout that is really a bug stays visible.
        logger.LogWarning(exception, "A dependency is unavailable ({ExceptionType}) for {Method} {Path}; answered 503", exception.GetType().Name,
            httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers[HeaderNames.RetryAfter] = DependencyOutage.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Title = "Service Unavailable" }
        });
    }
}
