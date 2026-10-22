using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Ariva.Api.Common.Middlewares;

/// <summary>
/// Adds the API security headers to every response, including error and challenge responses, just before the
/// response starts: <c>X-Content-Type-Options: nosniff</c>, <c>X-Frame-Options: DENY</c>,
/// <c>Referrer-Policy: no-referrer</c> and <c>Content-Security-Policy: default-src 'none'; base-uri 'none'; object-src 'none';
/// frame-ancestors 'none'</c> (CWE-79: JSON can never be sniffed or rendered as a page; base-uri does not fall back to
/// default-src, so it is named, and object-src is named for ASVS 5.0 V3.4.3, ARV-093). An endpoint that serves content other
/// than JSON (a floor plan image, ARV-018) may set a policy of its own; it is kept only when it still starts from
/// <c>default-src 'none'</c> and names <c>base-uri 'none'</c> and <c>frame-ancestors 'none'</c>, so no endpoint can loosen the
/// baseline by accident. Responses to requests that carry credentials get
/// <c>Cache-Control: no-store</c>. The <c>Server</c> header is removed here and switched off in Kestrel. A text answer that
/// names no charset gets <c>charset=utf-8</c> (ARV-092, <see cref="WithUtf8Charset"/>). An answer to a request whose declared
/// body is larger than its endpoint takes gets <c>Connection: close</c> (ARV-233, <see cref="CannotBeDrained"/>).
/// </summary>
/// <param name="next">The next middleware.</param>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    #region Constants

    /// <summary>Content security policy for API responses. An API that later serves HTML (OpenAPI UI) sets its own.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'";

    private const string ReferrerPolicyHeader = "Referrer-Policy";

    private const string AccessTokenQueryKey = "access_token";

    #endregion

    #region Middleware

    /// <summary>Registers the header callback and calls the next middleware.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task that completes when the rest of the pipeline has run.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(ApplyHeaders, context);
        return next(context);
    }

    #endregion

    #region Helpers

    private static Task ApplyHeaders(object state)
    {
        var context = (HttpContext)state;
        var headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers[ReferrerPolicyHeader] = "no-referrer";
        if (!IsLockedDown(headers.ContentSecurityPolicy))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        }
        headers.Remove(HeaderNames.Server);
        if (WithUtf8Charset(context.Response.ContentType) is { } contentType)
        {
            context.Response.ContentType = contentType;
        }

        if (CarriesCredentials(context))
        {
            headers.CacheControl = "no-store";
        }

        if (CannotBeDrained(context))
        {
            headers.Connection = "close";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// ARV-233: true when the request is HTTP/1.x and declares a body larger than its endpoint takes (the request's maximum body
    /// size, the endpoint's <c>[RequestSizeLimit]</c> or the server's). Its answer (413, or a refusal that never reads the body)
    /// leaves the body unread, and Kestrel will not read past the limit to reach the next request, so it closes the connection
    /// once the answer is sent. Without <c>Connection: close</c> in that answer a client keeps the connection for its next request,
    /// which then ends in a connection reset rather than an answer (Ingest's 413 to an oversized push, under load: ResponseEnded
    /// or an IOException on the request that reused it, ARV-124a's pilot run). With it, the client opens a new connection. HTTP/2
    /// and HTTP/3 refuse such a stream alone, so the connection is not at stake there.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns>True when the answer must close the connection.</returns>
    public static bool CannotBeDrained(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var protocol = context.Request.Protocol;
        return (HttpProtocol.IsHttp11(protocol) || HttpProtocol.IsHttp10(protocol))
               && context.Request.ContentLength is { } length
               && context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize is { } most
               && length > most;
    }

    /// <summary>
    /// The content type with <c>charset=utf-8</c> added when it is text that names no charset (ASVS 5.0 V4.1.1, ARV-092):
    /// every <c>text/*</c> type, <c>application/xml</c>, <c>application/javascript</c> and any <c>+xml</c> type. Ariva writes
    /// text only as UTF-8, and so do the framework's plain-text status pages (a client that accepts no JSON), the TickerQ
    /// dashboard's page and files and SignalR's server-sent events (whose registration allows only <c>utf-8</c> as the
    /// charset). Left as they are: a type that names its charset (Ariva's own CSV and AIDX answers do), JSON (UTF-8 by
    /// definition, RFC 8259, which defines no charset parameter; ASP.NET names it anyway), binary types and a value that does
    /// not parse.
    /// </summary>
    /// <param name="contentType">The response's Content-Type, or null.</param>
    /// <returns>The value to send instead, or null to leave the response's own.</returns>
    public static string WithUtf8Charset(string contentType)
    {
        if (string.IsNullOrEmpty(contentType) || contentType.Contains("charset", StringComparison.OrdinalIgnoreCase) ||
            !MediaTypeHeaderValue.TryParse(contentType, out var type) || type.Charset.HasValue)
        {
            return null;
        }

        var media = type.MediaType;
        var text = media.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                   || media.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
                   || media.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
                   || type.Suffix.Equals("xml", StringComparison.OrdinalIgnoreCase);
        if (!text)
        {
            return null;
        }

        type.Charset = "utf-8";
        return type.ToString();
    }

    /// <summary>
    /// True when an endpoint's own policy is at least as strict as the baseline's start: its first directive is exactly
    /// <c>default-src 'none'</c> and it holds <c>base-uri 'none'</c> and <c>frame-ancestors 'none'</c> as whole directives (a
    /// directive that lists a source beside <c>'none'</c> ignores the <c>'none'</c>, so it is not one of these).
    /// </summary>
    private static bool IsLockedDown(string policy)
    {
        if (string.IsNullOrEmpty(policy))
        {
            return false;
        }

        var directives = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return directives.Length > 0 && directives[0] == "default-src 'none'"
               && directives.Contains("base-uri 'none'", StringComparer.Ordinal)
               && directives.Contains("frame-ancestors 'none'", StringComparer.Ordinal);
    }

    private static bool CarriesCredentials(HttpContext context) =>
        context.User?.Identity?.IsAuthenticated == true
        || context.Request.Headers.ContainsKey(HeaderNames.Authorization)
        || context.Request.Query.ContainsKey(AccessTokenQueryKey);

    #endregion
}
