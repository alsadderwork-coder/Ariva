using System.Net;
using System.Text;
using System.Text.Json;
using Ariva.Api.Common.Filters;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-063: a NUL character never reaches a service. The dynamic scan's <c>siteCode=%00</c> reached PostgreSQL, which
/// refuses NUL in text, and answered 500. Now a NUL in the query string or a JSON string answers 400 on every
/// controller, after authentication (an anonymous caller still gets 401).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class NulCharacterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Options = new() { Converters = { new NulRejectingStringConverter() } };

    private sealed record Named(string Name, Dictionary<string, string> Tags);

    [Fact]
    public void Converter_Should_ReadStringsAndNulls_When_TheyHoldNoNul()
    {
        var value = JsonSerializer.Deserialize<Named>("""{"Name":"Arrivals hall","Tags":{"a":"b"}}""", Options);
        var empty = JsonSerializer.Deserialize<Named>("""{"Name":null,"Tags":null}""", Options);

        value.Name.Should().Be("Arrivals hall");
        value.Tags["a"].Should().Be("b");
        empty.Name.Should().BeNull();
        JsonSerializer.Serialize(value, Options).Should().Contain("Arrivals hall");
    }

    [Theory]
    [InlineData("""{"Name":"Arrivals\u0000hall","Tags":null}""")]
    [InlineData("""{"Name":"ok","Tags":{"a":"\u0000"}}""")]
    [InlineData("""{"Name":5,"Tags":null}""")]
    public void Converter_Should_Refuse_When_AStringHoldsANulOrIsNotAString(string json)
    {
        var read = () => JsonSerializer.Deserialize<Named>(json, Options);

        read.Should().Throw<JsonException>();
    }

    private static IArivaHost Host() =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            FakeAlertRules.Register(services);
        }));

    private static HttpRequestMessage AsAdministrator(HttpRequestMessage request)
    {
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "nul-admin");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);
        return request;
    }

    [Theory]
    [InlineData("/api/v1/admin/users?text=%00")]
    [InlineData("/api/v1/admin/users?text=ab%00cd")]
    [InlineData("/api/v1/admin/airports?siteCode=DMO&text=%00")]
    public async Task Query_Should_Answer400_When_AValueHoldsANul(string path)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await client.SendAsync(AsAdministrator(new HttpRequestMessage(HttpMethod.Get, path)), Ct);
        using var anonymous = await client.GetAsync(path, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("NUL character");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "default deny answers before any input check");
    }

    [Fact]
    public async Task Body_Should_Answer400_When_AJsonStringHoldsANul()
    {
        await using var app = Host();
        using var client = app.CreateClient();
        using var request = AsAdministrator(new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/alert-rules")
        {
            Content = new StringContent("""{"siteCode":"DMO","name":"Rule\u0000one"}""", Encoding.UTF8, "application/json")
        });

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("NUL character");
    }
}
