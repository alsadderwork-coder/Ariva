using System.Net;
using System.Text.Json;
using Ariva.Core.Security;
using Ariva.Core.Services.Border;
using Ariva.Core.Services.Topology;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Border;

/// <summary>
/// ARV-181a: GET api/v1/sites/{siteCode}/staffing-recommendation in process, its service replaced. The lane bound's refusal
/// answers 409 with its fixed text only (refused, never cut short, CWE-120); any other error answers 404 "Not found"
/// without a detail (CWE-204); an answer is the service's view model.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class StaffingRecommendationEndpointTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 14, 7, 0, DateTimeKind.Utc);

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(Result<StaffingRecommendationViewModel> answer)
    {
        var scope = new FixedSiteScope(new SiteAccess(false, new HashSet<string> { "DMO" }));
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => scope));
            services.Replace(ServiceDescriptor.Scoped<ISvcStaffingRecommendations>(_ => new FixedStaffing(answer)));
        }));
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sites/DMO/staffing-recommendation");
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "border-user");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.BorderShiftSupervisor);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_Should_AnswerConflictWithTheFixedTextOnly_When_TheProfileHasMoreLanesThanTheBound()
    {
        var (status, body) = await GetAsync(Result.Error<StaffingRecommendationViewModel>(StaffingErrors.TooManyLanes));

        status.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("detail").GetString().Should().Be(StaffingErrors.TooManyLanes);
        problem.RootElement.GetProperty("title").GetString().Should().Be("Conflict");
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(409);
        problem.RootElement.EnumerateObject().Select(p => p.Name).Should().NotContain(["lanes", "intervals", "siteCode", "errors"], "the fixed text only, no part answer");
    }

    [Theory]
    [InlineData(TopologyErrors.NotFound)]
    [InlineData(StaffingErrors.Stale)]
    [InlineData("anything else")]
    public async Task Get_Should_AnswerNotFoundWithoutADetail_When_TheServiceRefusesForAnyOtherReason(string error)
    {
        var (status, body) = await GetAsync(Result.Error<StaffingRecommendationViewModel>(error));

        status.Should().Be(HttpStatusCode.NotFound);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("title").GetString().Should().Be("Not found");
        problem.RootElement.TryGetProperty("detail", out _).Should().BeFalse("a 404 says nothing about why");
        body.Should().NotContain(error, "the service's reason is not echoed");
    }

    [Fact]
    public async Task Get_Should_AnswerTheServicesViewModel_When_ItAnswers()
    {
        var interval = new StaffingIntervalViewModel(Now, 8, 30, 3, "Wait", false, null, 6, 8, false, "None");
        var lane = new LaneStaffingViewModel("A-VIS", "DMO/A-VIS", "VIS", Now, 10, 0.85, 4, 2, false, 42, 2, "Measured", "None", [interval]);
        var (status, body) = await GetAsync(new Result<StaffingRecommendationViewModel>(new StaffingRecommendationViewModel("DMO", Now, 15, true, [lane])));

        status.Should().Be(HttpStatusCode.OK);
        using var answer = JsonDocument.Parse(body);
        answer.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["siteCode", "asOfUtc", "intervalMinutes", "indicative", "lanes"]);
        answer.RootElement.GetProperty("lanes")[0].GetProperty("intervals")[0].GetProperty("recommended").GetInt32().Should().Be(3);
    }

    private sealed class FixedStaffing(Result<StaffingRecommendationViewModel> answer) : ISvcStaffingRecommendations
    {
        public Task<Result<StaffingRecommendationViewModel>> GetAsync(string siteCode, CancellationToken ct = default) => Task.FromResult(answer);
    }

    private sealed class FixedSiteScope(SiteAccess access) : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(access);
    }
}
