using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010a in-process: the hosts authenticate Ariva access tokens from the Authorization header only, refuse anything
/// that is not ES256 at+jwt from their keys, and confine a pending account to the endpoints marked for it. Sign-in
/// itself needs the database and is covered in Ariva.IntegrationTests and the E2E suite.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SignInHostTests
{
    private const string SystemInfo = "/api/v1/system/info";
    private const string Logout = "/api/auth/logout";

    [Fact]
    public async Task Logout_Should_Return204_When_TokenIsValid()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, Logout, Issue(app, pending: false));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return403AccountPending_When_TokenHasPendingScope()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
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
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await client.PostAsync($"{Logout}?access_token={Issue(app, pending: false)}", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return401_When_TokenComesFromAnotherKey()
    {
        await using var main = ArivaHosts.Create(ArivaHosts.Main);
        await using var other = ArivaHosts.Create(ArivaHosts.Main);
        using var client = main.CreateClient();

        // Each in-process host has its own development key, so a token from one is foreign to the other.
        using var response = await SendAsync(client, HttpMethod.Post, Logout, Issue(other, pending: false));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer", "the challenge does not explain why the token failed");
    }

    [Fact]
    public async Task ProtectedEndpoint_Should_Return401_When_TokenIsUnsigned()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        using var client = app.CreateClient();
        var parts = Issue(app, pending: false).Split('.');

        using var response = await SendAsync(client, HttpMethod.Post, Logout, $"{Base64Url("{\"alg\":\"none\",\"typ\":\"at+jwt\"}")}.{parts[1]}.");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task UnknownRoute_Should_Return404_When_HostValidatesTheToken(string host)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();
        // Only Main registers the issuer; the other hosts hold public keys only, so sign with the host's development key.
        var settings = app.Services.GetRequiredService<Ariva.Infra.Settings.AuthSettings>();
        var keys = TokenKeys.Load(settings.Tokens, requireSigningKey: true);
        var token = new AccessTokenIssuer(keys, settings, TimeProvider.System)
            .Issue(Guid.CreateVersion7(), "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], false);

        using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/not-a-route", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{host} accepts its own valid token and routes the request");
    }

    private static string Issue(IArivaHost app, bool pending) =>
        app.Services.GetRequiredService<AccessTokenIssuer>()
            .Issue(Guid.CreateVersion7(), "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], pending);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Base64Url(string json) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(json);
}
