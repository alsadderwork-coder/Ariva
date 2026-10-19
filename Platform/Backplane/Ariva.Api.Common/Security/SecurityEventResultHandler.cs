using Ariva.Api.Common.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Logs the authorization middleware's refusals as security events (ARV-091, ASVS 5.0 V16.3.2) and hands every decision,
/// unchanged, to the handler it wraps (<see cref="StepUpResultHandler"/>, which hands the rest to the framework's), so every
/// answer stays exactly as it was. Here the request's principal is final (authenticated, its session checked, the policy's
/// own schemes merged in), so the request's log context takes the caller from it first (<see cref="RequestLogContext.Identify"/>).
/// A challenge (401) is <see cref="SecurityEvents.AuthorizationChallenged"/> with <see cref="SecurityEvents.NotAuthenticated"/>;
/// a refusal is written after the inner handler has answered, as the 401 of a missing second factor
/// (<see cref="SecurityEvents.SecondFactorRequired"/>) or as <see cref="SecurityEvents.AuthorizationForbidden"/> with the
/// failed requirements as the code names them. Hub method calls are authorized by SignalR itself and log their own refusals.
/// </summary>
/// <param name="inner">The handler that answers.</param>
/// <param name="events">The security event log.</param>
public sealed class SecurityEventResultHandler(IAuthorizationMiddlewareResultHandler inner, SecurityEventLog events) : IAuthorizationMiddlewareResultHandler
{
    #region Constants

    /// <summary>The longest requirement description written; a policy with more requirements is cut there.</summary>
    public const int MaxRequirementLength = 256;

    #endregion

    #region IAuthorizationMiddlewareResultHandler

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        RequestLogContext.Of(context)?.Identify(context.User);

        if (authorizeResult.Challenged)
        {
            events.Challenged(context, SecurityEvents.NotAuthenticated);
            await inner.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        await inner.HandleAsync(next, context, policy, authorizeResult);
        if (!authorizeResult.Forbidden)
            return;

        // The answer is decided: SecurityEventLog contains its own failures (counted as failed), so nothing here can turn the
        // 403 into a 500, and it works out the reason and the requirements only for an event the throttle lets through.
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
            events.Challenged(context, SecurityEvents.SecondFactorRequired);
        else
            events.Forbidden(context, authorizeResult.AuthorizationFailure);
    }

    #endregion

    #region Helpers

    /// <summary>The fixed reason for a refusal: the first kind of failed requirement that explains it.</summary>
    public static string ReasonOf(AuthorizationFailure failure)
    {
        var failed = failure?.FailedRequirements.ToList() ?? [];
        if (failed.Any(r => r is PermissionRequirement))
            return SecurityEvents.PermissionMissing;
        if (failed.Any(r => r is IntegrationScopeRequirement))
            return SecurityEvents.ScopeMissing;
        if (failed.Any(r => r is DeviceZoneRequirement))
            return SecurityEvents.DeviceZoneMismatch;
        return SecurityEvents.PolicyNotMet;
    }

    /// <summary>
    /// The failed requirements as the code names them: permission codes, an integration scope, a claim type, a requirement's
    /// type name. Everything here comes from endpoint metadata, nothing from the request.
    /// </summary>
    public static string RequirementsOf(AuthorizationFailure failure)
    {
        if (failure is null)
            return string.Empty;

        var names = failure.FailedRequirements.Select(requirement => requirement switch
        {
            PermissionRequirement permission => "Permission " + string.Join('|', permission.Permissions.Select(p => p.Code)),
            IntegrationScopeRequirement scope => "Scope " + scope.Scope,
            RecentMfaRequirement => "SecondFactor",
            ClaimsAuthorizationRequirement claim => "Claim " + claim.ClaimType,
            DenyAnonymousAuthorizationRequirement => "AuthenticatedUser",
            _ => requirement.GetType().Name
        }).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0 && failure.FailCalled)
            names.Add("Failed");

        var text = string.Join(", ", names);
        return text.Length <= MaxRequirementLength ? text : text[..MaxRequirementLength];
    }

    #endregion
}
