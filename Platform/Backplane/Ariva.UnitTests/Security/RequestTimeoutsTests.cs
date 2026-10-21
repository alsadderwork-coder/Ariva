using System.Globalization;
using System.Net;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Settings;
using Ariva.Core.Security;
using Ariva.Core.Services.Reports;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-096 (CWE-400, ASVS 5.0 V15.2.2): the request timeouts of Ariva.Api.Main. Every endpoint without its own policy has the default
/// (Proposed 120 s, 503 with Retry-After); the reports, the file transfers and the validation results name their own; the live hub
/// alone is exempt, since its connections last as long as a screen is open; every endpoint of the <c>upload</c> concurrency policy
/// has the <c>transfer</c> timeout; and the settings are refused at start when out of bounds. The other hosts' defaults (ARV-096b):
/// <see cref="HostRequestPathTests"/>.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class RequestTimeoutsTests
{
    #region Policies

    [Fact]
    public async Task Options_Should_HoldTheDefaultAndTheNamedPolicies_When_MainStarts()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var options = app.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        options.DefaultPolicy.Should().NotBeNull("every endpoint without its own policy gets the default");
        options.DefaultPolicy.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        options.DefaultPolicy.TimeoutStatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        options.Policies[RequestTimeoutsExtensions.ReportsPolicy].Timeout.Should().Be(TimeSpan.FromSeconds(60));
        options.Policies[RequestTimeoutsExtensions.ReportsPolicy].TimeoutStatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        options.Policies[RequestTimeoutsExtensions.TransferPolicy].Timeout.Should().Be(TimeSpan.FromMinutes(15));
        options.Policies[RequestTimeoutsExtensions.TransferPolicy].TimeoutStatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        options.Policies[Ariva.Infra.Settings.ValidationResultsSettings.RequestTimeoutPolicy].Timeout.Should().Be(TimeSpan.FromSeconds(90), "the validation results keep their own");
    }

    [Fact]
    public async Task Options_Should_ReadTheConfiguredValues_When_TheyAreSet()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder
            .UseSetting("Security:RequestTimeouts:DefaultSeconds", "90")
            .UseSetting("Security:RequestTimeouts:ReportsSeconds", "45")
            .UseSetting("Security:RequestTimeouts:TransferSeconds", "1200"));

        var options = app.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        options.DefaultPolicy.Timeout.Should().Be(TimeSpan.FromSeconds(90));
        options.Policies[RequestTimeoutsExtensions.ReportsPolicy].Timeout.Should().Be(TimeSpan.FromSeconds(45));
        options.Policies[RequestTimeoutsExtensions.TransferPolicy].Timeout.Should().Be(TimeSpan.FromSeconds(1200));
    }

    [Fact]
    public async Task Get_Should_Answer503WithRetryAfter_When_AnEndpointWithoutItsOwnPolicyOutlastsTheDefault()
    {
        // A report schedule read names no policy, so the default applies; the fake service waits until its token is cancelled.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedules = new Mock<ISvcReportSchedules>();
        schedules.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>(async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                return Fluentx.Result.Error<ReportScheduleViewModel>(ReportErrors.NotFound);
            });
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            builder.UseSetting("Security:RequestTimeouts:DefaultSeconds", "5");
            builder.ConfigureTestServices(services =>
            {
                TestAuthenticationHandler.Register(services);
                FakeAdministration.Register(services);
                services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
                services.Replace(ServiceDescriptor.Scoped<ISvcReportSchedules>(_ => schedules.Object));
            });
        });
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/report-schedules/{Guid.CreateVersion7()}");
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "schedule-reader");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.BorderShiftSupervisor);

        using var answer = await client.SendAsync(request, ct);

        answer.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        answer.Header("Retry-After").Should().Be(RequestTimeoutsSettings.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
        cancelled.Task.IsCompleted.Should().BeTrue("the action's token is the one the default timeout cancels");
    }

    #endregion

    #region Endpoints

    [Fact]
    public async Task Hub_Should_CarryTheDisableMetadata_When_Mapped()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var hub = RouteEndpoints(app).Where(e => Route(e).StartsWith(Ariva.Api.Main.Hubs.LiveHub.Path, StringComparison.Ordinal)).ToList();

        hub.Select(Route).Should().Contain(new[] { Ariva.Api.Main.Hubs.LiveHub.Path, Ariva.Api.Main.Hubs.LiveHub.Path + "/negotiate" }, "the hub maps its connection and its negotiate");
        hub.Should().OnlyContain(e => e.Metadata.GetMetadata<DisableRequestTimeoutAttribute>() != null,
            "a hub connection lasts as long as a screen is open, so the default timeout must not cut it");
    }

    [Fact]
    public async Task Endpoints_Should_ExemptOnlyTheLiveHub_When_MainMapsThem()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var exempt = RouteEndpoints(app).Where(e => e.Metadata.GetMetadata<DisableRequestTimeoutAttribute>() != null).Select(Route).ToList();

        exempt.Should().NotBeEmpty().And.OnlyContain(route => route.StartsWith(Ariva.Api.Main.Hubs.LiveHub.Path, StringComparison.Ordinal),
            "every other endpoint has the default or its own timeout");
    }

    [Fact]
    public async Task Endpoints_Should_NameOnlyRegisteredPolicies_When_TheyChooseTheirOwn()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        var registered = app.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value.Policies.Keys;

        var named = RouteEndpoints(app).Select(e => e.Metadata.GetMetadata<RequestTimeoutAttribute>()?.PolicyName).Where(n => n != null).Distinct().ToList();

        named.Should().BeEquivalentTo(new[] { RequestTimeoutsExtensions.ReportsPolicy, RequestTimeoutsExtensions.TransferPolicy,
            Ariva.Infra.Settings.ValidationResultsSettings.RequestTimeoutPolicy });
        named.Should().OnlyContain(n => registered.Contains(n), "an unregistered policy name fails the request at run time");
        RouteEndpoints(app).Should().NotContain(e => e.Metadata.GetMetadata<RequestTimeoutAttribute>() != null && e.Metadata.GetMetadata<RequestTimeoutAttribute>().PolicyName == null,
            "a timeout is chosen by a policy name, so its value lives in the settings");
    }

    [Fact]
    public async Task Endpoints_Should_GiveEveryUploadAndWholeFileTheTransferPolicy_When_MainMapsThem()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        var endpoints = RouteEndpoints(app);

        var uploads = endpoints.Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitingExtensions.UploadPolicy).ToList();
        var transfers = endpoints.Where(e => e.Metadata.GetMetadata<RequestTimeoutAttribute>()?.PolicyName == RequestTimeoutsExtensions.TransferPolicy).ToList();

        uploads.Should().HaveCountGreaterThanOrEqualTo(10, "floor plans, SSIM, both CSV imports, the site configuration and advertisements");
        uploads.Should().OnlyContain(e => transfers.Contains(e), "an upload or import may take longer than the default (the upload policy's endpoints, ARV-096)");
        transfers.Except(uploads).Select(Action).Should().BeEquivalentTo(new[] { "FloorPlans.Content", "AdvertisingMedia.Content", "DisplayAdvertising.Content" },
            "beyond the uploads, only the stored files served whole take the transfer timeout");
    }

    [Fact]
    public async Task Endpoints_Should_GiveBothReportsTheReportsPolicies_When_MainMapsThem()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var reports = RouteEndpoints(app).Where(e => Action(e).StartsWith("Reports.", StringComparison.Ordinal)).ToList();

        reports.Select(Action).Should().BeEquivalentTo(new[] { "Reports.Daily", "Reports.DailyCsv" });
        reports.Should().OnlyContain(e => e.Metadata.GetMetadata<RequestTimeoutAttribute>().PolicyName == RequestTimeoutsExtensions.ReportsPolicy);
        reports.Should().OnlyContain(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>().PolicyName == RateLimitingExtensions.ReportsPolicy);
    }

    #endregion

    #region Settings

    [Theory]
    [InlineData(4, 60, 900, "DefaultSeconds")]
    [InlineData(601, 60, 900, "DefaultSeconds")]
    [InlineData(120, 4, 900, "ReportsSeconds")]
    [InlineData(120, 601, 900, "ReportsSeconds")]
    [InlineData(120, 60, 59, "TransferSeconds")]
    [InlineData(120, 60, 3_601, "TransferSeconds")]
    public void Problems_Should_RefuseAValue_When_ItIsOutOfBounds(int defaultSeconds, int reportsSeconds, int transferSeconds, string named)
    {
        var settings = new RequestTimeoutsSettings { DefaultSeconds = defaultSeconds, ReportsSeconds = reportsSeconds, TransferSeconds = transferSeconds };

        settings.Problems().Should().ContainSingle().Which.Should().StartWith(named);
        settings.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 5, 60)]
    [InlineData(600, 600, 3_600)]
    [InlineData(120, 60, 900)]
    public void Problems_Should_BeEmpty_When_TheValuesAreWithinTheirBounds(int defaultSeconds, int reportsSeconds, int transferSeconds)
    {
        new RequestTimeoutsSettings { DefaultSeconds = defaultSeconds, ReportsSeconds = reportsSeconds, TransferSeconds = transferSeconds }.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Security:RequestTimeouts:DefaultSeconds", "0", "*Security:RequestTimeouts*")]
    [InlineData("Security:RequestTimeouts:ReportsSeconds", "601", "*Security:RequestTimeouts*")]
    [InlineData("Security:RequestTimeouts:TransferSeconds", "30", "*Security:RequestTimeouts*")]
    [InlineData("Security:RateLimiting:Reports:PermitLimit", "0", "*Security:RateLimiting*")]
    [InlineData("Security:RateLimiting:Reports:QueueLimit", "-1", "*Security:RateLimiting*")]
    public async Task CreateClient_Should_FailAtStartup_When_ATimeoutOrTheReportsPolicyIsOutOfBounds(string key, string value, string message)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.UseSetting(key, value));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage(message);
    }

    [Fact]
    public void BaseFile_Should_HoldTheCodeDefaults_When_Read()
    {
        // The Proposed values (docs/product/decisions.md, ARV-096) are written out in the base file, as the other limits are.
        var file = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Api.Common", "appsettings.base.json"))
            .Build();

        var timeouts = file.GetSection(RequestTimeoutsSettings.SectionName).Get<RequestTimeoutsSettings>();
        var reports = file.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>().Reports;
        var defaults = new RequestTimeoutsSettings();

        (timeouts.DefaultSeconds, timeouts.ReportsSeconds, timeouts.TransferSeconds).Should().Be((120, 60, 900));
        (defaults.DefaultSeconds, defaults.ReportsSeconds, defaults.TransferSeconds).Should().Be((120, 60, 900), "the code default matches the file");
        (reports.PermitLimit, reports.QueueLimit).Should().Be((2, 4));
        (new RateLimitingSettings().Reports.PermitLimit, new RateLimitingSettings().Reports.QueueLimit).Should().Be((2, 4), "the code default matches the file");
    }

    #endregion

    #region Helpers

    private static List<RouteEndpoint> RouteEndpoints(IArivaHost app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    private static string Route(RouteEndpoint endpoint) => "/" + endpoint.RoutePattern.RawText?.TrimStart('/');

    /// <summary>A controller action as <c>Controller.Action</c> (the controller's name without its suffix), or the endpoint's name.</summary>
    private static string Action(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action ? $"{action.ControllerName}.{action.ActionName}" : endpoint.DisplayName ?? string.Empty;

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }

    #endregion
}
