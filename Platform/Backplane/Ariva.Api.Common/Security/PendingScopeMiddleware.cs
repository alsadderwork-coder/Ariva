using Ariva.Infra.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Common.Security;

/// <summary>Marks the endpoints a pending account may call: change password, TOTP enrolment, logout.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AllowPendingScopeAttribute : Attribute;

/// <summary>
/// An account with a temporary password or without TOTP (scope "pending") gets 403 everywhere except endpoints marked
/// <see cref="AllowPendingScopeAttribute"/> and anonymous endpoints (ADR-0026). Runs after authentication and before
/// authorization, so no permission check or handler ever sees a pending caller.
/// </summary>
public sealed class PendingScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var pending = context.User.Identity?.IsAuthenticated == true &&
                      context.User.FindFirst(ArivaClaims.Scope)?.Value == ArivaClaims.PendingScope;
        var endpoint = context.GetEndpoint();
        if (pending &&
            endpoint?.Metadata.GetMetadata<AllowPendingScopeAttribute>() is null &&
            endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Finish setting up your account",
                Type = "https://ariva/problems/account-pending",
                Detail = "Change your temporary password and enrol an authenticator before using Ariva."
            }, options: null, contentType: "application/problem+json");
            return;
        }

        await next(context);
    }
}

public static class PendingScopeExtensions
{
    /// <summary>Call right after UseAuthentication.</summary>
    public static IApplicationBuilder UsePendingScope(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<PendingScopeMiddleware>();
    }
}
