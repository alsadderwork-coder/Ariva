using System.Security.Claims;
using System.Text.Encodings.Web;
using Ariva.Core.Services.Displays;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Display player authentication (ARV-058): a scheme of its own, apart from users, devices and integration clients. A
/// player presents its display's code in the query (<c>code</c>) and the display's credential in the
/// <c>X-Ariva-Display-Key</c> header. Endpoints opt in with <see cref="DisplayAuthenticatedAttribute"/> and accept nothing
/// else, so a user token there answers 401, and a display credential anywhere else answers 401 too.
/// </summary>
public static class DisplayAuthentication
{
    public const string Scheme = "Ariva.Display";
    public const string Policy = "Ariva.Display";
    public const string KeyHeader = "X-Ariva-Display-Key";
    public const string CodeQuery = "code";
    public const string DisplayIdClaim = "ariva:display_id";
    public const string DisplayCodeClaim = "ariva:display";
    public const string CredentialPrefixClaim = "ariva:display_credential";

    public static AuthenticationBuilder AddArivaDisplayAuthentication(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddScheme<AuthenticationSchemeOptions, DisplayAuthenticationHandler>(Scheme, displayName: null, configureOptions: null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(DisplayIdClaim));
        return builder;
    }

    /// <summary>The presented credential, or null when the request carries none (one header value only).</summary>
    public static string Presented(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.TryGetValue(KeyHeader, out var header) && header.Count == 1 ? header[0] : null;
    }

    /// <summary>
    /// The rate limit partition of a player request: a hash of the whole presented credential when it is well formed
    /// (never the prefix, which administrators see), the client address otherwise.
    /// </summary>
    public static string RateLimitPartition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = Presented(context.Request);
        if (DisplayCredentials.IsWellFormed(key))
            return "display:" + DeviceCredentials.Hash(key)[..32];
        var address = context.Connection.RemoteIpAddress;
        return "address:" + (address is null ? "unknown" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString());
    }

    /// <summary>The authenticated display's id and the prefix of the credential it presented, or nulls for anyone else.</summary>
    public static (Guid? Id, string CredentialPrefix) DisplayOf(ClaimsPrincipal user) =>
        user?.Identity is { IsAuthenticated: true, AuthenticationType: Scheme } && Guid.TryParse(user.FindFirstValue(DisplayIdClaim), out var id)
            ? (id, user.FindFirstValue(CredentialPrefixClaim))
            : (null, null);
}

/// <summary>Marks an endpoint for display players only (ARV-058).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DisplayAuthenticatedAttribute : AuthorizeAttribute
{
    public DisplayAuthenticatedAttribute()
    {
        Policy = DisplayAuthentication.Policy;
        AuthenticationSchemes = DisplayAuthentication.Scheme;
    }
}

/// <summary>
/// Checks a presented display credential (ARV-058): well formed, the live and enabled display with that code and the
/// credential's prefix found, the SHA-256 compared in constant time (a miss costs the same hash). The caller learns
/// nothing about why (401); the log names the code and the reason, never the credential.
/// </summary>
public sealed class DisplayAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = DisplayAuthentication.Presented(Request);
        if (key is null)
            return AuthenticateResult.NoResult();
        var code = Request.Query.TryGetValue(DisplayAuthentication.CodeQuery, out var values) && values.Count == 1 ? values[0] : null;
        var player = await Context.RequestServices.GetRequiredService<ISvcDisplayBoard>().FindAsync(code, key, Context.RequestAborted);
        if (player is null)
        {
            // The code only when it is a code (CWE-117); never the credential.
            Logger.LogWarning("Display authentication refused for display {Display} from {Address}", Ariva.Core.Domain.Entities.TopologyCodes.IsValid(code) ? code : "(invalid)",
                Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("Display authentication failed.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(DisplayAuthentication.DisplayIdClaim, player.Id.ToString()),
            new Claim(DisplayAuthentication.DisplayCodeClaim, player.Code),
            new Claim(DisplayAuthentication.CredentialPrefixClaim, player.CredentialPrefix)
        ], DisplayAuthentication.Scheme, DisplayAuthentication.DisplayCodeClaim, null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), DisplayAuthentication.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "ArivaDisplay realm=\"ariva-displays\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
