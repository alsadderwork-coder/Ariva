using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// Test only authentication scheme: a request that carries <see cref="UserHeader"/> is authenticated as that user,
/// any other request stays anonymous and is still challenged by the Ariva.Deny scheme. It lets tests reach the 404,
/// 405 and 500 answers that default deny hides from anonymous callers. Never registered outside tests.
/// </summary>
/// <param name="options">The scheme options.</param>
/// <param name="logger">The logger factory.</param>
/// <param name="encoder">The URL encoder.</param>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    #region Constants

    /// <summary>The scheme name.</summary>
    public const string SchemeName = "Test";

    /// <summary>Request header that names the authenticated test user.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Optional request header with comma separated role codes for the test user (permission tests).</summary>
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>Optional request header with comma separated amr values (step-up tests, ARV-010d).</summary>
    public const string MethodsHeader = "X-Test-Amr";

    /// <summary>Optional request header with the auth_time in Unix seconds (step-up tests, ARV-010d).</summary>
    public const string AuthTimeHeader = "X-Test-Auth-Time";

    #endregion

    #region Registration

    /// <summary>
    /// Adds the scheme and makes it the default authenticate scheme; the challenge scheme stays Ariva.Deny.
    /// Use it with <c>ConfigureTestServices</c>.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    public static void Register(IServiceCollection services)
    {
        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(SchemeName, displayName: null, configureOptions: null);
        services.PostConfigure<AuthenticationOptions>(authentication => authentication.DefaultAuthenticateScheme = SchemeName);
    }

    #endregion

    #region Authentication

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        if (Request.Headers.TryGetValue(MethodsHeader, out var methods))
        {
            claims.AddRange(methods.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(method => new Claim("amr", method)));
        }

        if (Request.Headers.TryGetValue(AuthTimeHeader, out var authTime))
        {
            claims.Add(new Claim("auth_time", authTime.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    #endregion
}
