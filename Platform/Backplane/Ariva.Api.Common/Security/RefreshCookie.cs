using Ariva.Api.Common.Settings;
using Ariva.Core.Domain.InputModels;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>
/// The refresh token cookie (ADR-0026, ARV-010b): <c>__Secure-ariva_rt</c>, HttpOnly, Secure, SameSite=Strict,
/// Path=/api/auth, Max-Age the session's remaining absolute lifetime. The value never appears in a response body or a
/// log. Refresh also needs <c>X-Ariva-Csrf: 1</c> and an allow-listed Origin (CWE-352): a cross-site form cannot set
/// the header, and a script on another origin is refused by name.
/// </summary>
public static class RefreshCookie
{
    public const string CsrfHeader = "X-Ariva-Csrf";

    public static void Write(HttpResponse response, string token, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(RefreshTokens.CookieName, token, Options(lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero));
    }

    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(RefreshTokens.CookieName, string.Empty, Options(TimeSpan.Zero));
    }

    public static string Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.TryGetValue(RefreshTokens.CookieName, out var value) ? value : null;
    }

    /// <summary>The sign-in context for the service: client address (after trusted forwarded headers), user agent, cookie.</summary>
    public static SignInContext ContextOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SignInContext(
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            Read(context.Request));
    }

    /// <summary>True when the request carries <c>X-Ariva-Csrf: 1</c> and an Origin from Security:Cors:AllowedOrigins.</summary>
    public static bool PassesCsrfCheck(HttpRequest request, IOptions<CorsSettings> cors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(cors);

        if (!string.Equals(request.Headers[CsrfHeader].ToString(), "1", StringComparison.Ordinal))
            return false;

        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0 && (cors.Value.AllowedOrigins ?? []).Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase));
    }

    private static CookieOptions Options(TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = RefreshTokens.CookiePath,
        MaxAge = maxAge,
        IsEssential = true
    };
}
