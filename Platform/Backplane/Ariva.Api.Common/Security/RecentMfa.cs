using System.Globalization;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Step-up for critical actions (ADR-0026, ARV-010d): the caller must have proved a second factor (amr otp or rc)
/// within the last <paramref name="minutes"/> minutes (auth_time). Otherwise the answer is 401 with
/// <c>WWW-Authenticate: Bearer error="insufficient_user_authentication", max_age=900</c> (RFC 9470) and a problem
/// with <c>error: mfa_required</c>; POST /api/auth/step-up then gives a token with a fresh auth_time. Every endpoint
/// that carries it is listed in security/critical-actions.json, and an architecture test keeps the two in step.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequiresRecentMfaAttribute : AuthorizeAttribute
{
    public const int DefaultMinutes = 15;

    public RequiresRecentMfaAttribute(int minutes = DefaultMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        MaxAge = TimeSpan.FromMinutes(minutes);
        Policy = RecentMfaRequirement.PolicyPrefix + (int)MaxAge.TotalSeconds;
    }

    public TimeSpan MaxAge { get; }
}

/// <summary>A second factor within <see cref="MaxAge"/>.</summary>
public sealed class RecentMfaRequirement(TimeSpan maxAge) : IAuthorizationRequirement
{
    public const string PolicyPrefix = "RecentMfa:";

    /// <summary>amr values that prove a second factor: a TOTP code or a recovery code.</summary>
    public static readonly IReadOnlySet<string> SecondFactors = new HashSet<string>(StringComparer.Ordinal) { "otp", "rc" };

    public TimeSpan MaxAge { get; } = maxAge;

    /// <summary>Builds the policy for "RecentMfa:&lt;seconds&gt;"; null for any other name.</summary>
    public static AuthorizationPolicy PolicyFor(string policyName)
    {
        if (policyName is null || !policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal) ||
            !int.TryParse(policyName[PolicyPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            return null;
        }

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new RecentMfaRequirement(TimeSpan.FromSeconds(seconds)))
            .Build();
    }
}

/// <summary>Succeeds when the token's amr holds otp or rc and its auth_time is within the requirement's age; otherwise leaves the requirement pending.</summary>
public sealed class RecentMfaHandler(TimeProvider timeProvider) : AuthorizationHandler<RecentMfaRequirement>
{
    public const string ErrorCode = "mfa_required";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RecentMfaRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var user = context.User;
        var secondFactor = user.FindAll(ArivaClaims.Methods).Any(c => RecentMfaRequirement.SecondFactors.Contains(c.Value));
        var recent = long.TryParse(user.FindFirst(ArivaClaims.AuthTime)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var authTime) &&
                     timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(authTime) <= requirement.MaxAge;

        // No context.Fail on refusal: an explicit fail empties FailedRequirements, and StepUpResultHandler needs them
        // to tell "only the second factor is missing" (401) from "the permission is missing as well" (403).
        if (secondFactor && recent)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Turns a refusal caused only by <see cref="RecentMfaRequirement"/> into the RFC 9470 401 answer. A caller who also
/// lacks the permission gets the ordinary 403, so step-up never reveals that an action exists for someone who may not
/// perform it. Everything else goes to the framework's handler.
/// </summary>
public sealed class StepUpResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string ProblemType = "https://ariva/problems/mfa-required";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        var failed = authorizeResult.AuthorizationFailure?.FailedRequirements.ToList() ?? [];
        if (!authorizeResult.Forbidden || authorizeResult.AuthorizationFailure.FailCalled || failed.Count == 0 || !failed.All(r => r is RecentMfaRequirement))
            return _default.HandleAsync(next, context, policy, authorizeResult);

        var maxAge = (int)failed.OfType<RecentMfaRequirement>().Min(r => r.MaxAge).TotalSeconds;
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"insufficient_user_authentication\", error_description=\"A recent second factor is required\", max_age={maxAge.ToString(CultureInfo.InvariantCulture)}";
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Confirm it is you",
            Type = ProblemType,
            Detail = "Enter the code from your authenticator app (POST /api/auth/step-up) and try again."
        };
        problem.Extensions["error"] = RecentMfaHandler.ErrorCode;
        problem.Extensions["max_age"] = maxAge;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}
