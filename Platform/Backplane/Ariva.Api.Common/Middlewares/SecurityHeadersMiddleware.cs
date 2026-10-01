using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Ariva.Api.Common.Middlewares;

/// <summary>
/// Adds the API security headers to every response, including error and challenge responses, just before the
/// response starts: <c>X-Content-Type-Options: nosniff</c>, <c>X-Frame-Options: DENY</c>,
/// <c>Referrer-Policy: no-referrer</c> and <c>Content-Security-Policy: default-src 'none'; frame-ancestors 'none'</c>
/// (CWE-79: JSON can never be sniffed or rendered as a page). An endpoint that serves content other than JSON (a
/// floor plan image, ARV-018) may set a policy of its own; it is kept only when it still starts from
/// <c>default-src 'none'</c>, so no endpoint can loosen the baseline by accident. Responses to requests that carry credentials get
/// <c>Cache-Control: no-store</c>. The <c>Server</c> header is removed here and switched off in Kestrel.
/// </summary>
/// <param name="next">The next middleware.</param>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    #region Constants

    /// <summary>Content security policy for API responses. An API that later serves HTML (OpenAPI UI) sets its own.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

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

        if (CarriesCredentials(context))
        {
            headers.CacheControl = "no-store";
        }

        return Task.CompletedTask;
    }

    private static bool IsLockedDown(string policy) =>
        !string.IsNullOrEmpty(policy) && policy.StartsWith("default-src 'none'", StringComparison.Ordinal) && policy.Contains("frame-ancestors 'none'", StringComparison.Ordinal);

    private static bool CarriesCredentials(HttpContext context) =>
        context.User?.Identity?.IsAuthenticated == true
        || context.Request.Headers.ContainsKey(HeaderNames.Authorization)
        || context.Request.Query.ContainsKey(AccessTokenQueryKey);

    #endregion
}
