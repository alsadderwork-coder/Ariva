using Ariva.Core.Security;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Ariva.Api.Common.Security;

/// <summary>
/// ARV-010b: every request with an Ariva access token is checked against its server-side session (sid), so logout, a
/// disabled account or a revoked refresh family end access within 5 seconds (4 second session cache) instead of at
/// token expiry (CWE-613). Runs right after authentication. A token whose session is not active gets 401 with
/// <c>error: session_expired</c>; on an anonymous endpoint the caller simply continues as anonymous. Principals without
/// a subject (the test authentication scheme) have no session and are not checked.
/// </summary>
public sealed class SessionValidationMiddleware(RequestDelegate next)
{
    public const string ProblemType = "https://ariva/problems/session-expired";

    public async Task InvokeAsync(HttpContext context, ISessionValidator validator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validator);

        var user = context.User;
        if (user.Identity?.IsAuthenticated == true && user.FindFirst(ArivaClaims.Subject) is not null)
        {
            var active = Guid.TryParse(user.FindFirst(ArivaClaims.SessionId)?.Value, out var sessionId) &&
                         await validator.CheckAsync(sessionId, context.RequestAborted) == SessionState.Active;
            if (!active)
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity());
                }
                else
                {
                    await WriteExpiredAsync(context);
                    return;
                }
            }
        }

        await next(context);
    }

    private static Task WriteExpiredAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Your session has ended",
            Type = ProblemType,
            Detail = "Sign in again."
        };
        problem.Extensions["error"] = "session_expired";
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}

public static class SessionValidationExtensions
{
    /// <summary>Call right after UseAuthentication, before UsePendingScope.</summary>
    public static IApplicationBuilder UseSessionValidation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SessionValidationMiddleware>();
    }
}
