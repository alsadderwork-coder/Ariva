using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Authentication handler for the <see cref="ArivaAuthenticationSchemes.Deny"/> placeholder scheme. It ignores
/// every credential (bearer headers, query string tokens, cookies) and returns <see cref="AuthenticateResult.NoResult"/>,
/// so the authorization fallback policy challenges and the caller gets 401 with <c>WWW-Authenticate: Bearer</c>.
/// </summary>
/// <param name="options">The scheme options.</param>
/// <param name="logger">The logger factory.</param>
/// <param name="encoder">The URL encoder.</param>
public sealed class DenyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    #region Authentication

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    #endregion
}
