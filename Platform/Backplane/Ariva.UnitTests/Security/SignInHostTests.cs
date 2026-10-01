using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Security;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010a and ARV-010b in-process: the hosts authenticate Ariva access tokens from the Authorization header only,
/// refuse anything that is not ES256 at+jwt from their keys, refuse tokens whose session is not active, confine a
/// pending account to the endpoints marked for it, and handle the refresh cookie and its CSRF check. The database
/// work (sessions, rotation, reuse) is covered in Ariva.IntegrationTests and the E2E suite; here the session check and
/// the sign-in service are fakes.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SignInHostTests
{
    private const string SystemInfo = "/api/v1/system/info";
    private const string Logout = "/api/auth/logout";
    private const string Refresh = "/api/auth/refresh";
    private const string WebOrigin = "http://localhost:51011";
    private const string CookieName = "__Secure-ariva_rt";

    private readonly FakeSessionValidator _sessions = new();
    private readonly FakeAuthenticator _authenticator = new();

    #region Tokens

    [Fact]
    public async Task Logout_Should_Return204AndClearTheCookie_When_TokenIsValid()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, Logout, Issue(app, pending: false));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cookie = SetCookie(response);
        cookie.Should().StartWith(CookieName + "=;");
        AttributesOf(cookie).Should().Contain("max-age=0").And.Contain("path=/api/auth");
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return401SessionExpired_When_SessionIsNotActive()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var sessionId = Guid.CreateVersion7();
        _sessions[sessionId] = SessionState.Revoked;
        var token = Issue(app, pending: false, sessionId);

        using var refused = await SendAsync(client, HttpMethod.Post, Logout, token);
        using var probe = await SendAsync(client, HttpMethod.Get, "/health/readiness", token);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refused.Headers.WwwAuthenticate.ToString().Should().Be("Bearer error=\"invalid_token\"");
        using (var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            body.RootElement.GetProperty("error").GetString().Should().Be("session_expired");
            body.RootElement.GetProperty("type").GetString().Should().Be("https://ariva/problems/session-expired");
        }

        probe.StatusCode.Should().Be(HttpStatusCode.OK, "an anonymous endpoint treats a dead token as no token");
    }

    [Theory]
    [InlineData(SessionState.Expired)]
    [InlineData(SessionState.Unknown)]
    public async Task ProtectedEndpoint_Should_Return401_When_SessionIsExpiredOrUnknown(SessionState state)
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var sessionId = Guid.CreateVersion7();
        _sessions[sessionId] = state;

        using var response = await SendAsync(client, HttpMethod.Post, Logout, Issue(app, pending: false, sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return401_When_TokenHasNoSessionId()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var keys = app.Services.GetRequiredService<TokenKeys>();
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "ariva",
            Audience = "ariva-users",
            Expires = DateTime.UtcNow.AddMinutes(5),
            TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = Guid.CreateVersion7().ToString(), ["name"] = "officer.one" },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(keys.SigningKey, "ES256")
        });

        using var response = await SendAsync(client, HttpMethod.Post, Logout, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "every Ariva token names its session");
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return403AccountPending_When_TokenHasPendingScope()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var token = Issue(app, pending: true);

        using var blocked = await SendAsync(client, HttpMethod.Get, SystemInfo, token);
        using var logout = await SendAsync(client, HttpMethod.Post, Logout, token);
        using var probe = await SendAsync(client, HttpMethod.Get, "/health/readiness", token);

        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        blocked.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using (var body = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            body.RootElement.GetProperty("type").GetString().Should().Be("https://ariva/problems/account-pending");
        }

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent, "logout is marked AllowPendingScope");
        probe.StatusCode.Should().Be(HttpStatusCode.OK, "anonymous endpoints stay open to a pending caller");
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return401_When_TokenIsInTheQueryString()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await client.PostAsync($"{Logout}?access_token={Issue(app, pending: false)}", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return401_When_TokenComesFromAnotherKey()
    {
        await using var main = Host(ArivaHosts.Main);
        await using var other = Host(ArivaHosts.Main);
        using var client = main.CreateClient();

        // Each in-process host has its own development key, so a token from one is foreign to the other.
        using var response = await SendAsync(client, HttpMethod.Post, Logout, Issue(other, pending: false));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer", "the challenge does not explain why the token failed");
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return401_When_TokenIsUnsigned()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var parts = Issue(app, pending: false).Split('.');

        using var response = await SendAsync(client, HttpMethod.Post, Logout, $"{Base64Url("{\"alg\":\"none\",\"typ\":\"at+jwt\"}")}.{parts[1]}.");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task UnknownRoute_Should_Return404_When_HostValidatesTheTokenAndSession(string host)
    {
        await using var app = Host(host);
        using var client = app.CreateClient();
        // Only Main registers the issuer; the other hosts hold public keys only, so sign with the host's development key.
        var settings = app.Services.GetRequiredService<Ariva.Infra.Settings.AuthSettings>();
        var keys = TokenKeys.Load(settings.Tokens, requireSigningKey: true);
        var issuer = new AccessTokenIssuer(keys, settings, TimeProvider.System);
        var revoked = Guid.CreateVersion7();
        _sessions[revoked] = SessionState.Revoked;

        using var accepted = await SendAsync(client, HttpMethod.Get, "/api/v1/not-a-route",
            issuer.Issue(Guid.CreateVersion7(), "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], false));
        using var refused = await SendAsync(client, HttpMethod.Get, "/api/v1/not-a-route",
            issuer.Issue(Guid.CreateVersion7(), "officer.one", revoked, Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], false));

        accepted.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{host} accepts its own valid token and routes the request");
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{host} checks the session of every token");
    }

    #endregion

    #region Refresh cookie

    [Fact]
    public async Task Login_Should_SetTheRefreshCookieAndKeepItOutOfTheBody_When_SignInSucceeds()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var refreshToken = RefreshTokens.New();
        _authenticator.NextSignIn = new SignInResult(new TokenViewModel("access", "Bearer", 900, null), refreshToken, TimeSpan.FromHours(12));

        using var response = await client.PostAsync("/api/auth/login", Json(new { userName = "officer.one", password = "violet tram ladder 9031" }), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        body.Should().NotContain(refreshToken);
        var cookie = SetCookie(response);
        cookie.Should().StartWith($"{CookieName}={refreshToken};");
        var attributes = AttributesOf(cookie);
        attributes.Should().Contain("max-age=43200").And.Contain("path=/api/auth").And.Contain("secure").And.Contain("samesite=strict").And.Contain("httponly");
        attributes.Should().NotContain("domain=", "the cookie stays on the host that set it");
    }

    [Fact]
    public async Task Login_Should_PassThePresentedCookieToTheService_When_BrowserStillHoldsOne()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var old = RefreshTokens.New();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = Json(new { userName = "officer.one", password = "x-x-x-x-x-x-x" }) };
        request.Headers.Add("Cookie", $"{CookieName}={old}");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _authenticator.Contexts.Should().ContainSingle().Which.PresentedRefreshToken.Should().Be(old, "the service revokes it (CWE-384)");
    }

    [Fact]
    public async Task Login_Should_Return401MfaRequired_When_PasswordIsRightButTheCodeIsMissing()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        _authenticator.LoginError = ISvcAuthenticator.MfaRequired;

        using var response = await client.PostAsync("/api/auth/login", Json(new { userName = "officer.one", password = "violet tram ladder 9031" }), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("error").GetString().Should().Be("mfa_required");
        body.RootElement.GetProperty("type").GetString().Should().Be("https://ariva/problems/mfa-required");
        SetCookie(response).Should().BeEmpty("no session starts before the second factor");
    }

    [Theory]
    [InlineData(null, WebOrigin)]
    [InlineData("0", WebOrigin)]
    [InlineData("1", "https://attacker.example")]
    [InlineData("1", null)]
    public async Task Refresh_Should_Return403_When_CsrfHeaderOrOriginIsWrong(string csrf, string origin)
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(RefreshRequest(RefreshTokens.New(), csrf, origin), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _authenticator.Contexts.Should().BeEmpty("the service is never reached");
    }

    [Fact]
    public async Task Refresh_Should_RotateTheCookie_When_CsrfCheckPassesAndServiceAccepts()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();
        var presented = RefreshTokens.New();
        var successor = RefreshTokens.New();
        _authenticator.NextSignIn = new SignInResult(new TokenViewModel("access", "Bearer", 900, null), successor, TimeSpan.FromHours(11));

        using var response = await client.SendAsync(RefreshRequest(presented, "1", WebOrigin), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SetCookie(response).Should().StartWith($"{CookieName}={successor};");
        AttributesOf(SetCookie(response)).Should().Contain("max-age=39600");
        _authenticator.Contexts.Should().ContainSingle().Which.PresentedRefreshToken.Should().Be(presented);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain(successor);
    }

    [Fact]
    public async Task Refresh_Should_Return401SessionExpiredAndClearTheCookie_When_ServiceRefuses()
    {
        await using var app = Host(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(RefreshRequest(RefreshTokens.New(), "1", WebOrigin), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("session_expired");
        AttributesOf(SetCookie(response)).Should().Contain("max-age=0");
    }

    #endregion

    #region Helpers

    private IArivaHost Host(string name) =>
        ArivaHosts.Create(name, configure: builder => builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ISessionValidator>(_sessions));
            services.Replace(ServiceDescriptor.Singleton<ISvcAuthenticator>(_authenticator));
        }));

    private static string Issue(IArivaHost app, bool pending, Guid? sessionId = null) =>
        app.Services.GetRequiredService<AccessTokenIssuer>()
            .Issue(Guid.CreateVersion7(), "officer.one", sessionId ?? Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], pending);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage RefreshRequest(string cookie, string csrf, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Refresh);
        request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        if (csrf is not null)
            request.Headers.Add("X-Ariva-Csrf", csrf);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return request;
    }

    private static string SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Single(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal))
            : string.Empty;

    /// <summary>The cookie attributes after the value, lower case.</summary>
    private static string AttributesOf(string setCookie) => setCookie[(setCookie.IndexOf(';', StringComparison.Ordinal) + 1)..].ToLowerInvariant();

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    private static string Base64Url(string json) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(json);

    #endregion
}
