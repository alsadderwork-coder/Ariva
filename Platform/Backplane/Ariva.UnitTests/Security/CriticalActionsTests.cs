using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Services.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010d: critical actions need a second factor within 15 minutes. security/critical-actions.json and the
/// [RequiresRecentMfa] endpoints must agree, endpoints guarded by a critical permission must carry the attribute, and a
/// refusal is the RFC 9470 401 with max_age, never for a caller who also lacks the permission.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class CriticalActionsTests
{
    private const string RecoveryCodes = "/api/auth/totp/recovery-codes";

    private static readonly Lazy<CriticalActions> Loaded = new(() =>
        JsonSerializer.Deserialize<CriticalActions>(File.ReadAllText(RepositoryPaths.Resolve("security/critical-actions.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

    #region The list and the endpoints

    [Fact]
    public void CriticalPermissions_Should_NameRealPermissions_When_ListIsRead()
    {
        Loaded.Value.MaxAgeMinutes.Should().Be(RequiresRecentMfaAttribute.DefaultMinutes);
        Loaded.Value.CriticalPermissions.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
        Loaded.Value.CriticalPermissions.Should().OnlyContain(name => Global.Defaults.Permissions.Find(name) != null);
        Loaded.Value.Routes.Should().OnlyContain(route => Loaded.Value.Categories.Contains(route.Category), "every route belongs to a PRD category");
    }

    [Fact]
    public async Task Endpoints_Should_MatchTheCriticalList_When_EveryHostIsBooted()
    {
        var problems = new List<string>();
        var guarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var critical = Loaded.Value.CriticalPermissions.ToHashSet(StringComparer.Ordinal);

        foreach (var host in ArivaHosts.Backplane)
        {
            await using var app = ArivaHosts.Create(host);
            foreach (var endpoint in app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
            {
                var recentMfa = endpoint.Metadata.GetOrderedMetadata<RequiresRecentMfaAttribute>();
                var permissions = endpoint.Metadata.GetOrderedMetadata<PermissionAttribute>().SelectMany(p => p.PermissionNames).ToList();
                foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                {
                    var key = $"{host} {method} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";
                    if (recentMfa.Count > 0)
                        guarded.Add(key);
                    if (recentMfa.Any(a => a.MaxAge > TimeSpan.FromMinutes(Loaded.Value.MaxAgeMinutes)))
                        problems.Add($"{key}: allows more than {Loaded.Value.MaxAgeMinutes} minutes");
                    if (recentMfa.Count == 0 && permissions.Any(critical.Contains))
                        problems.Add($"{key}: guarded by {string.Join("/", permissions)} (critical) but has no [RequiresRecentMfa]");
                }
            }
        }

        var listed = Loaded.Value.Routes.Select(r => $"{r.Host} {r.Method} {r.Route}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        problems.AddRange(guarded.Where(key => !listed.Contains(key)).Select(key => $"{key}: [RequiresRecentMfa] but not in security/critical-actions.json"));
        problems.AddRange(listed.Where(key => !guarded.Contains(key)).Select(key => $"{key}: listed as critical but not mapped with [RequiresRecentMfa]"));

        guarded.Should().NotBeEmpty();
        problems.Should().BeEmpty();
    }

    #endregion

    #region The handler

    [Theory]
    [InlineData("pwd,otp", 0, true)]
    [InlineData("pwd,rc", 14 * 60, true)]
    [InlineData("pwd,otp", 15 * 60, true)]
    [InlineData("pwd,otp", 15 * 60 + 1, false)]
    [InlineData("pwd", 0, false)]
    [InlineData("", 0, false)]
    public async Task Handler_Should_RequireASecondFactorWithinTheAge_When_Evaluated(string methods, int ageSeconds, bool succeeds)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        var claims = methods.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => new Claim("amr", m)).ToList();
        claims.Add(new Claim("auth_time", (clock.GetUtcNow().ToUnixTimeSeconds() - ageSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var requirement = new RecentMfaRequirement(TimeSpan.FromMinutes(15));
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), null);

        await new RecentMfaHandler(clock).HandleAsync(context);

        context.HasSucceeded.Should().Be(succeeds);
    }

    [Fact]
    public async Task Handler_Should_Refuse_When_AuthTimeIsMissingOrNotANumber()
    {
        var requirement = new RecentMfaRequirement(TimeSpan.FromMinutes(15));
        foreach (var claims in new[] { new[] { new Claim("amr", "otp") }, [new Claim("amr", "otp"), new Claim("auth_time", "soon")] })
        {
            var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), null);
            await new RecentMfaHandler(TimeProvider.System).HandleAsync(context);
            context.HasSucceeded.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("RecentMfa:900", 900)]
    [InlineData("RecentMfa:60", 60)]
    public void PolicyFor_Should_BuildTheRequirement_When_NameIsWellFormed(string name, int seconds)
    {
        var policy = RecentMfaRequirement.PolicyFor(name);

        policy.Requirements.OfType<RecentMfaRequirement>().Single().MaxAge.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("RecentMfa:")]
    [InlineData("RecentMfa:-5")]
    [InlineData("RecentMfa:0")]
    [InlineData("RecentMfa:9x")]
    [InlineData("Permission:EditUser")]
    public void PolicyFor_Should_ReturnNull_When_NameIsNotARecentMfaPolicy(string name)
    {
        RecentMfaRequirement.PolicyFor(name).Should().BeNull();
    }

    [Fact]
    public async Task ResultHandler_Should_Answer403_When_ThePermissionIsMissingToo()
    {
        var context = ResultContext();
        var failure = AuthorizationFailure.Failed([new RecentMfaRequirement(TimeSpan.FromMinutes(15)), new DenyAnonymousAuthorizationRequirement()]);

        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        await new StepUpResultHandler().HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid(failure));

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden, "step-up never reveals an action to a caller who may not perform it");
        context.Response.Headers.WWWAuthenticate.ToString().Should().NotContain("insufficient_user_authentication");
    }

    [Fact]
    public async Task ResultHandler_Should_Answer401WithTheShortestMaxAge_When_OnlyTheSecondFactorIsMissing()
    {
        var context = ResultContext();
        var failure = AuthorizationFailure.Failed([new RecentMfaRequirement(TimeSpan.FromMinutes(15)), new RecentMfaRequirement(TimeSpan.FromMinutes(5))]);

        await new StepUpResultHandler().HandleAsync(_ => Task.CompletedTask, context, null, PolicyAuthorizationResult.Forbid(failure));

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().Should().EndWith("max_age=300");
    }

    private static DefaultHttpContext ResultContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null, null);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), Response = { Body = new MemoryStream() } };
    }

    #endregion

    #region Over HTTP

    [Fact]
    public async Task CriticalEndpoint_Should_Return401WithMaxAge_When_SecondFactorIsOld()
    {
        await using var app = MainHost(out _);
        using var client = app.CreateClient();
        var old = DateTimeOffset.UtcNow.AddMinutes(-16).ToUnixTimeSeconds();

        using var response = await client.SendAsync(Request(RecoveryCodes, "pwd,otp", old), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"insufficient_user_authentication\"").And.Contain("max_age=900");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        problem.RootElement.GetProperty("error").GetString().Should().Be("mfa_required");
        problem.RootElement.GetProperty("max_age").GetInt32().Should().Be(900);
        problem.RootElement.GetProperty("type").GetString().Should().Be(StepUpResultHandler.ProblemType);
    }

    [Fact]
    public async Task CriticalEndpoint_Should_Return401_When_OnlyAPasswordWasUsed()
    {
        await using var app = MainHost(out _);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request(RecoveryCodes, "pwd", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CriticalEndpoint_Should_ReachTheAction_When_SecondFactorIsRecent()
    {
        await using var app = MainHost(out _);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request(RecoveryCodes, "pwd,otp", DateTimeOffset.UtcNow.AddMinutes(-14).ToUnixTimeSeconds()), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the gate passes; the fake service then refuses the code");
    }

    [Fact]
    public async Task NonCriticalEndpoint_Should_NeverAskForStepUp_When_SecondFactorIsOld()
    {
        await using var app = MainHost(out _);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request("/api/auth/totp/confirm", "pwd", 0), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [Fact]
    public async Task StepUp_Should_ReturnBadRequest_When_TheCodeIsRefused()
    {
        await using var app = MainHost(out _);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request("/api/auth/step-up", "pwd", 0), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static IArivaHost MainHost(out FakeAuthenticator authenticator)
    {
        var fake = new FakeAuthenticator();
        authenticator = fake;
        return ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder
            .UseSetting("Security:RateLimiting:Auth:PermitLimit", "1000")
            .ConfigureTestServices(services =>
            {
                TestAuthenticationHandler.Register(services);
                services.Replace(ServiceDescriptor.Scoped<ISvcAuthenticator>(_ => fake));
            }));
    }

    private static HttpRequestMessage Request(string path, string methods, long authTime)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"code":"000000"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "step-up-user");
        request.Headers.Add(TestAuthenticationHandler.MethodsHeader, methods);
        request.Headers.Add(TestAuthenticationHandler.AuthTimeHeader, authTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    #endregion

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record CriticalActions(int MaxAgeMinutes, List<string> Categories, List<string> CriticalPermissions, List<CriticalRoute> Routes);

    private sealed record CriticalRoute(string Host, string Method, string Route, string Category);
}
