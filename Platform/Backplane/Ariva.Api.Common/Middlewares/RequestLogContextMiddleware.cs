using Ariva.Api.Common.Logging;
using Ariva.Api.Common.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Context;

namespace Ariva.Api.Common.Middlewares;

/// <summary>
/// Puts a <see cref="RequestLogContext"/> on Serilog's log context for the rest of the request (ARV-091), so every event the
/// request writes names the client address and, once the authorization middleware has decided, the caller. Added by
/// <c>UseAppForwardedHeaders</c> right after the forwarded headers middleware, so every Backplane host has it and the address
/// is the client the trusted proxy names.
/// <para>
/// When the request ends with 401, 403 or 429 and no security event was recorded for it, it writes the generic
/// <see cref="SecurityEvents.RequestRefused"/> (ASVS 5.0 V16.3.2, V16.3.3): the refusals made outside the authorization
/// middleware and the rate limiter (an ended or revoked session, a pending account, a service's 403, a device on the wrong
/// transport, an integration client's own limit, the upload limit) are then logged too, without touching the code that made
/// them or their answers. The caller is taken from the request's principal, or the checked integration client, when the
/// authorization middleware did not run.
/// </para>
/// <para>
/// When an upload (a request the upload policy's filter marked, <see cref="RequestLogContext.MarkUpload"/>) ends with a status
/// <see cref="SecurityEvents.UploadRefusedReasonOf"/> names (400, 404, 408, 409, 413, 415, 422) and no security event was
/// recorded for it, it writes <see cref="SecurityEvents.UploadRefused"/> (ARV-203): the refusals of the services and the
/// controllers (a file that is not a type the upload takes, too large, malformed, a stale token, a target not found, a
/// conflict), throttled like every request-level event.
/// </para>
/// </summary>
/// <param name="next">The rest of the pipeline.</param>
public sealed class RequestLogContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var log = new RequestLogContext(context.Connection.RemoteIpAddress);
        context.Features.Set(log);
        using (LogContext.Push(log))
        {
            await next(context);
            if (log.Recorded)
                return;
            var status = context.Response.StatusCode;
            if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status429TooManyRequests)
                Record(context, log, static (events, request) => events.Refused(request));
            else if (log.UploadKind is not null && SecurityEvents.UploadRefusedReasonOf(status) is not null)
                Record(context, log, static (events, request) => events.UploadRefused(request));
        }
    }

    /// <summary>A refusal event written when the request ends; the answer is already given, so a failure here is contained and changes nothing.</summary>
    private static void Record(HttpContext context, RequestLogContext log, Action<SecurityEventLog, HttpContext> write)
    {
        try
        {
            if (!log.Identified)
                log.Identify(context.User, IntegrationAuthentication.CallerOf(context)?.ClientId);
            if (context.RequestServices?.GetService<SecurityEventLog>() is { } events)
                write(events, context);
        }
#pragma warning disable CA1031 // the answer is written; a security event never turns it into an error
        catch (Exception)
#pragma warning restore CA1031
        {
            // SecurityEventLog counts its own failures; nothing else here can fail but a broken principal, which is not logged.
        }
    }
}
