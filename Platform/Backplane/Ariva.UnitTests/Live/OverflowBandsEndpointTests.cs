using System.Net;
using System.Text.Json;
using Ariva.Core.Security;
using Ariva.Core.Services.Live;
using Ariva.Core.Services.Topology;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Live;

/// <summary>
/// ARV-120: GET api/v1/sites/{siteCode}/overflow-bands in process, its service replaced: the bound's refusal answers 409 with
/// its fixed text (never a cut answer, CWE-120), any other error 404 without a detail, and an answer has no truncated field.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class OverflowBandsEndpointTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 18, 10, 0, DateTimeKind.Utc);

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(Result<OverflowBandsViewModel> answer)
    {
        var scope = new FixedSiteScope(new SiteAccess(false, new HashSet<string> { "DMO" }));
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => scope));
            services.Replace(ServiceDescriptor.Scoped<ISvcOverflowBands>(_ => new FixedOverflowBands(answer)));
        }));
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sites/DMO/overflow-bands");
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "live-user");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.HandlerStationManager);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_Should_AnswerConflictWithItsFixedText_When_TheBandsAreMoreThanOneAnswerHolds()
    {
        var (status, body) = await GetAsync(Result.Error<OverflowBandsViewModel>(OverflowBandErrors.TooMany));

        status.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("detail").GetString().Should().Be(OverflowBandErrors.TooMany);
        problem.RootElement.GetProperty("title").GetString().Should().Be("Conflict");
        body.Should().NotContain("zones");
    }

    [Theory]
    [InlineData(TopologyErrors.NotFound)]
    [InlineData("anything else")]
    public async Task Get_Should_AnswerNotFoundWithoutADetail_When_TheServiceRefusesForAnyOtherReason(string error)
    {
        var (status, body) = await GetAsync(Result.Error<OverflowBandsViewModel>(error));

        status.Should().Be(HttpStatusCode.NotFound);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.TryGetProperty("detail", out _).Should().BeFalse("a 404 says nothing about why");
    }

    [Fact]
    public async Task Get_Should_AnswerTheZonesWithoutATruncatedField_When_TheReadIsWithinItsBound()
    {
        var zone = new OverflowZoneViewModel("A-VIS", OverflowBandStates.Occupied, [new OverflowBandViewModel("A-OV", OverflowBandStates.Occupied, Now, 12)]);
        var (status, body) = await GetAsync(new Result<OverflowBandsViewModel>(new OverflowBandsViewModel("DMO", Now, ISvcOverflowBands.FreshMinutes, [zone])));

        status.Should().Be(HttpStatusCode.OK);
        using var answer = JsonDocument.Parse(body);
        answer.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["siteCode", "asOfUtc", "freshMinutes", "zones"]);
        answer.RootElement.GetProperty("zones")[0].GetProperty("bands")[0].GetProperty("maxOccupancy").GetInt32().Should().Be(12);
    }

    private sealed class FixedOverflowBands(Result<OverflowBandsViewModel> answer) : ISvcOverflowBands
    {
        public Task<Result<OverflowBandsViewModel>> GetAsync(string siteCode, CancellationToken ct = default) => Task.FromResult(answer);
    }

    private sealed class FixedSiteScope(SiteAccess access) : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(access);
    }
}
