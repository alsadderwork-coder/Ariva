using System.Net;
using System.Security.Claims;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Integration;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-042: integration clients. Credentials have their shapes; what a client may do is checked (known scopes, bound
/// sites, CIDR networks); every change of it invalidates the client's tokens; integration tokens come from a ring and an
/// audience of their own, so neither kind of token verifies as the other; an endpoint's scope and the route's site must
/// be among the token's.
/// </summary>
public sealed class IntegrationClientTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);

    private static IntegrationClientAccess Access(params string[] scopes)
    {
        var (access, errors) = IntegrationClientAccess.Check(scopes.Length == 0 ? [IntegrationScopes.FlightsWrite] : scopes, ["DMO"], ["10.20.0.0/24"], false);
        errors.Should().BeEmpty();
        return access;
    }

    private static IntegrationClient Client() =>
        new(IntegrationCredentials.NewClientId(), "AODB of DMO", IntegrationClientKind.Aodb, Access(), new Core.Security.PasswordHashValue("pbkdf2-sha256", 600_000, "c2FsdA==", "aGFzaA=="),
            "protected-seed", Now);

    [Fact]
    public void Credentials_Should_HaveTheirShapes_When_Generated()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => IntegrationCredentials.NewClientId()).ToList();
        ids.Should().OnlyContain(id => IntegrationClient.IsClientId(id)).And.OnlyHaveUniqueItems();
        var secret = IntegrationCredentials.NewSecret();
        IntegrationCredentials.IsWellFormed(secret).Should().BeTrue();
        secret.Should().HaveLength(IntegrationCredentials.SecretLength).And.StartWith("ics_");
        IntegrationCredentials.IsWellFormed(secret[..^1] + "!").Should().BeFalse();
        IntegrationCredentials.IsWellFormed(secret + "A").Should().BeFalse();
        IntegrationClient.IsClientId("ic_AAAAAAAAAAAAAAAAAAAAAAAAAA").Should().BeFalse("lower case only");
        IntegrationClient.IsClientId("ic_aaaaaaaaaaaaaaaaaaaaaaaaa1").Should().BeFalse("base32 has no 1");
        IntegrationClient.IsClientId(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "flights:write", "admin" }, new[] { "DMO" }, new string[0], "Scopes")]
    [InlineData(new string[0], new[] { "DMO" }, new string[0], "Scopes")]
    [InlineData(new[] { "Flights:Write" }, new[] { "DMO" }, new string[0], "Scopes")]
    [InlineData(new[] { "flights:write" }, new string[0], new string[0], "site codes")]
    [InlineData(new[] { "flights:write" }, new[] { "dmo" }, new string[0], "site codes")]
    [InlineData(new[] { "flights:write" }, new[] { "DMO" }, new[] { "10.0.0.1/24" }, "host bits")]
    [InlineData(new[] { "flights:write" }, new[] { "DMO" }, new[] { "0.0.0.0/0" }, "/0")]
    [InlineData(new[] { "flights:write" }, new[] { "DMO" }, new[] { "10.0.0.1" }, "CIDR")]
    [InlineData(new[] { "flights:write" }, new[] { "DMO" }, new string[0], "allowed source networks")]
    [InlineData(new[] { "flights:write" }, new[] { "DMO" }, new[] { "::ffff:10.0.0.0/104" }, "IPv4-mapped")]
    public void Access_Should_BeRefused_When_ItIsNotWhatAClientMayHold(string[] scopes, string[] sites, string[] networks, string why)
    {
        var (access, errors) = IntegrationClientAccess.Check(scopes, sites, networks, false);

        access.Should().BeNull();
        errors.Should().Contain(e => e.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public void Access_Should_BeNormalised_When_Checked()
    {
        var (access, _) = IntegrationClientAccess.Check(["queues:read", "flights:write", "flights:write"], ["ZZ9", "DMO", "DMO"], ["10.20.0.0/24", " 10.20.0.0/24 ", "2001:db8::/32"], true);

        access.Scopes.Should().Equal("flights:write", "queues:read");
        access.Sites.Should().Equal("DMO", "ZZ9");
        access.Networks.Should().Equal("10.20.0.0/24", "2001:db8::/32");
        SourceNetworks.Contains(access.Networks, IPAddress.Parse("::ffff:10.20.0.9")).Should().BeTrue("an IPv4-mapped address counts as IPv4");
        SourceNetworks.Contains(access.Networks, IPAddress.Parse("10.21.0.9")).Should().BeFalse();
    }

    [Fact]
    public void Client_Should_InvalidateItsTokens_When_WhatItMayDoChanges()
    {
        var client = Client();
        client.TokensValidFromUtc.Should().Be(Now);
        var version = client.TokenVersion;

        client.Apply(Access(), Now.AddMinutes(1)).Should().BeFalse("nothing changed");
        client.TokensValidFromUtc.Should().Be(Now);
        client.Apply(Access(IntegrationScopes.FlightsWrite, IntegrationScopes.AllocationsWrite), Now.AddMinutes(2)).Should().BeTrue();
        client.TokensValidFromUtc.Should().Be(Now.AddMinutes(2));
        client.TokenVersion.Should().Be(version + 1, "every change that stops tokens bumps the version");
        client.SetSecret(new Core.Security.PasswordHashValue("pbkdf2-sha256", 600_000, "b3RoZXI=", "b3RoZXI="), Now.AddMinutes(3));
        client.TokensValidFromUtc.Should().Be(Now.AddMinutes(3));
        client.SetTotp("other-seed", Now.AddMinutes(4));
        client.Should().Match<IntegrationClient>(c => c.TokensValidFromUtc == Now.AddMinutes(4) && c.TotpLastStep == null);
        client.Disable(Now.AddMinutes(5)).Should().BeTrue();
        client.Should().Match<IntegrationClient>(c => c.TokensValidFromUtc == Now.AddMinutes(5) && c.Status == IntegrationClientStatus.Disabled);
        client.Disable(Now.AddMinutes(6)).Should().BeFalse();
        client.Enable().Should().BeTrue();
        client.HasScope(IntegrationScopes.AllocationsWrite).Should().BeTrue();
        client.Serves("DMO").Should().BeTrue();
        client.Serves("BEY").Should().BeFalse();
        var badName = () => client.Rename("AODB\r\nBcc");
        badName.Should().Throw<ArgumentException>();
    }

    private static TokenSettings Ring(string directory, string audience, string file) => new()
    {
        Issuer = "ariva",
        Audience = audience,
        UseDevelopmentKeys = true,
        DevelopmentKeyDirectory = directory,
        DevelopmentKeyFile = file
    };

    [Fact]
    public async Task Tokens_Should_NotVerifyAcrossRings_When_AUserOrIntegrationTokenIsPresented()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ariva-unit-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Auth:IntegrationTokens:UseDevelopmentKeys"] = "true" }).Build();
            var integrationSettings = IntegrationTokenSettings.From(configuration, Ring(directory, "ariva-users", "token-signing-dev.key"));
            integrationSettings.Audience.Should().Be("ariva-integration");
            integrationSettings.DevelopmentKeyFile.Should().NotBe("token-signing-dev.key");
            var ring = new IntegrationTokenKeys(TokenKeys.Load(integrationSettings, requireSigningKey: true), integrationSettings);
            var userSettings = Ring(directory, "ariva-users", "token-signing-dev.key");
            var userKeys = TokenKeys.Load(userSettings, requireSigningKey: true);
            ring.Keys.SigningKey.KeyId.Should().NotBe(userKeys.SigningKey.KeyId, "a ring of its own");

            var (token, expires) = new IntegrationTokenIssuer(ring, TimeProvider.System).Issue("ic_aaaaaaaaaaaaaaaaaaaaaaaaaa", Guid.NewGuid(), 3, ["flights:write"], ["DMO", "AUH"]);
            expires.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(10));
            var handler = new JsonWebTokenHandler();
            var asIntegration = await handler.ValidateTokenAsync(token, IntegrationTokenIssuer.ValidationParameters(ring));
            asIntegration.IsValid.Should().BeTrue();
            asIntegration.ClaimsIdentity.FindAll(IntegrationClaims.Site).Select(c => c.Value).Should().Equal("DMO", "AUH");
            asIntegration.ClaimsIdentity.FindFirst(IntegrationClaims.Version)!.Value.Should().Be("3");
            (await handler.ValidateTokenAsync(token, AccessTokenIssuer.ValidationParameters(userKeys, userSettings))).IsValid.Should().BeFalse("not a user token");

            var userToken = new AccessTokenIssuer(userKeys, new AuthSettings { Tokens = userSettings }, TimeProvider.System)
                .Issue(Guid.NewGuid(), "someone", Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, ["pwd"], false);
            (await handler.ValidateTokenAsync(userToken, IntegrationTokenIssuer.ValidationParameters(ring))).IsValid.Should().BeFalse("not an integration token");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("flights:write queues:read", "DMO", "flights:write", "DMO", true)]
    [InlineData("flights:write", "DMO", "immigration:write", "DMO", false)]
    [InlineData("flights:write", "DMO", "flights:write", "BEY", false)]
    [InlineData("flights:write", "DMO", "flights:write", null, false)]
    [InlineData("flights:writer", "DMO", "flights:write", "DMO", false)]
    public async Task Scope_Should_HoldTheEndpointsScopeAndSite_When_Authorized(string clientScopes, string clientSite, string endpointScope, string routeSite, bool allowed)
    {
        // The token's own claims say everything is allowed: only the client's record, as the scheme checked it, counts.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(IntegrationClaims.ClientId, "ic_aaaaaaaaaaaaaaaaaaaaaaaaaa"), new Claim(IntegrationClaims.Scope, string.Join(' ', IntegrationScopes.All)),
                new Claim(IntegrationClaims.Site, "BEY"), new Claim(IntegrationClaims.Site, "DMO")],
            IntegrationAuthentication.Scheme));
        var http = new DefaultHttpContext();
        http.Items[IntegrationAuthentication.CallerItem] = new Core.Domain.ViewModels.IntegrationCallerViewModel("ic_aaaaaaaaaaaaaaaaaaaaaaaaaa", "AODB",
            clientScopes.Split(' '), [clientSite], false);
        if (routeSite is not null)
            http.Request.RouteValues[IntegrationAuthentication.SiteRouteValue] = routeSite;
        var context = new AuthorizationHandlerContext([new IntegrationScopeRequirement(endpointScope)], user, http);

        await new IntegrationScopeHandler().HandleAsync(context);

        context.HasSucceeded.Should().Be(allowed);
        var withoutCaller = new AuthorizationHandlerContext([new IntegrationScopeRequirement(endpointScope)], user, new DefaultHttpContext());
        await new IntegrationScopeHandler().HandleAsync(withoutCaller);
        withoutCaller.HasSucceeded.Should().BeFalse("no checked caller, no access");
        var attribute = () => new IntegrationScopeAttribute("admin:all");
        attribute.Should().Throw<ArgumentException>();
        http.GetRouteValue(IntegrationAuthentication.SiteRouteValue).Should().Be(routeSite);
    }
}
