using System.Net;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Reports;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Reports;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-092 (ASVS 5.0 V5.4.1 and V4.1.1) on Ariva.Api.Main in process, the services replaced: a floor plan and an
/// advertisement item (the library preview and the display player's fetch) go out inline under the server's name for the
/// record and its stored type (the services keep the uploader's name only as a label, and the content answers never see
/// it), an SVG plan as <c>image/svg+xml</c> with <c>charset=utf-8</c>, and the endpoint's locked-down policy and nosniff
/// still on the answer after the security headers middleware. A daily report section, the one file Ariva sends to be
/// saved, is an attachment under a name the server builds, as <c>text/csv; charset=utf-8</c>.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class StoredFileDownloadTests
{
    #region Fields

    private const string PlanPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox; frame-ancestors 'none'";

    private const string MediaPolicy = "default-src 'none'; sandbox; frame-ancestors 'none'";

    private static readonly Guid LevelId = Guid.Parse("0199a000-0000-7000-8000-0000000000b1");

    private static readonly Guid ItemId = Guid.Parse("0199a000-0000-7000-8000-0000000000c2");

    #endregion

    #region Tests

    [Theory]
    [InlineData("image/png", "png", "image/png")]
    [InlineData("image/jpeg", "jpg", "image/jpeg")]
    [InlineData("image/svg+xml", "svg", "image/svg+xml; charset=utf-8")]
    public async Task FloorPlanContent_Should_GoOutInlineUnderTheServersName_When_ThePlanIsStored(string stored, string extension, string sent)
    {
        var sha256 = new string('a', 64);
        var plans = new Mock<ISvcFloorPlans>(MockBehavior.Strict);
        plans.Setup(p => p.OpenAsync(LevelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Result<FloorPlanContent>(new FloorPlanContent(new MemoryStream("<svg/>"u8.ToArray()), stored, sha256)));
        await using var app = MainHost(services => services.Replace(ServiceDescriptor.Scoped<ISvcFloorPlans>(_ => plans.Object)));

        using var response = await GetAsync(app, $"/api/v1/admin/levels/{LevelId}/floor-plan/content", RoleCodes.BorderShiftSupervisor);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Header("Content-Disposition").Should().Be($"inline; filename=\"floor-plan-{LevelId:D}.{extension}\"");
        response.Header("Content-Type").Should().Be(sent);
        response.Header("Content-Security-Policy").Should().Be(PlanPolicy);
        response.Header("X-Content-Type-Options").Should().Be("nosniff");
        response.Header("ETag").Should().Be($"\"{sha256}\"");
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/webp", "webp")]
    [InlineData("video/mp4", "mp4")]
    [InlineData("video/webm", "webm")]
    public async Task AdvertisingMediaContent_Should_GoOutInlineUnderTheServersName_When_TheItemIsStored(string stored, string extension)
    {
        var media = new Mock<ISvcAdvertisingMedia>(MockBehavior.Strict);
        media.Setup(m => m.OpenAsync("DMO", ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Result<AdvertisingMediaContent>(new AdvertisingMediaContent(new MemoryStream([1, 2, 3]), stored, new string('b', 64))));
        var policies = new Mock<ISvcAdvertisingPolicies>(MockBehavior.Strict);
        policies.Setup(p => p.AccessAsync("DMO", It.IsAny<CancellationToken>())).ReturnsAsync(AdvertisingAccess.Allowed);
        var scope = new SiteAccess(false, new HashSet<string> { "DMO" });
        await using var app = MainHost(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new FixedSiteScope(scope)));
            services.Replace(ServiceDescriptor.Scoped<ISvcAdvertisingPolicies>(_ => policies.Object));
            services.Replace(ServiceDescriptor.Scoped<ISvcAdvertisingMedia>(_ => media.Object));
        });

        using var response = await GetAsync(app, $"/api/v1/admin/sites/DMO/advertising/media/{ItemId}/content", RoleCodes.TerminalDutyManager);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Header("Content-Disposition").Should().Be($"inline; filename=\"advertisement-{ItemId:D}.{extension}\"");
        response.Header("Content-Type").Should().Be(stored, "a binary type goes out as stored");
        response.Header("Content-Security-Policy").Should().Be(MediaPolicy);
        response.Header("X-Content-Type-Options").Should().Be("nosniff");
        response.Header("Cache-Control").Should().Contain("no-store");
    }

    [Theory]
    [InlineData("image/webp", "webp")]
    [InlineData("video/mp4", "mp4")]
    public async Task DisplayAdvertisingContent_Should_GoOutInlineUnderTheServersName_When_APlayerFetchesAnItem(string stored, string extension)
    {
        // The display player's fetch (ARV-142a) answers through the same helper as the library preview, under its own scheme.
        var credential = DisplayCredentials.New();
        var displayId = Guid.Parse("0199a000-0000-7000-8000-0000000000d3");
        var board = new Mock<ISvcDisplayBoard>(MockBehavior.Strict);
        board.Setup(b => b.FindAsync("GATE-4", credential.Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DisplayPlayer(displayId, "GATE-4", "DMO", credential.Prefix));
        var media = new Mock<ISvcAdvertisingMedia>(MockBehavior.Strict);
        media.Setup(m => m.OpenForDisplayAsync(displayId, credential.Prefix, ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Result<AdvertisingMediaContent>(new AdvertisingMediaContent(new MemoryStream([1, 2, 3]), stored, new string('c', 64))));
        await using var app = MainHost(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<ISvcDisplayBoard>(_ => board.Object));
            services.Replace(ServiceDescriptor.Scoped<ISvcAdvertisingMedia>(_ => media.Object));
        });
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/display/advertising/{ItemId}?code=GATE-4");
        request.Headers.Add(DisplayAuthentication.KeyHeader, credential.Credential);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Header("Content-Disposition").Should().Be($"inline; filename=\"advertisement-{ItemId:D}.{extension}\"");
        response.Header("Content-Type").Should().Be(stored);
        response.Header("Content-Security-Policy").Should().Be(MediaPolicy);
        response.Header("X-Content-Type-Options").Should().Be("nosniff");
        response.Header("Cache-Control").Should().Contain("no-store");
    }

    [Theory]
    [InlineData(null, "hours")]
    [InlineData("ALERTS", "alerts")]
    [InlineData("devices", "devices")]
    public async Task DailyReportCsv_Should_BeAnAttachmentUnderTheServersName_When_ASectionIsDownloaded(string section, string part)
    {
        // The one download Ariva sends as a file to save (ARV-060): named from the report's own site code, the checked date
        // and the allowlisted section, never from the query as sent (the section's case, for example).
        var day = new DateOnly(2026, 10, 18);
        var report = new DailyReport("DMO", day, "Asia/Amman", new DateTime(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc),
            new DailyHeadline(null, null, null, 0, 0, 0, null), [], new Dictionary<string, int>(), [], []);
        var reports = new Mock<ISvcReports>(MockBehavior.Strict);
        reports.Setup(r => r.DailyAsync("DMO", day, It.IsAny<CancellationToken>())).ReturnsAsync(() => new Result<DailyReport>(report));
        var scope = new SiteAccess(false, new HashSet<string> { "DMO" });
        await using var app = MainHost(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new FixedSiteScope(scope)));
            services.Replace(ServiceDescriptor.Scoped<ISvcReports>(_ => reports.Object));
        });
        var query = section is null ? string.Empty : $"&section={section}";

        using var response = await GetAsync(app, $"/api/v1/sites/DMO/reports/daily.csv?date=2026-10-18{query}", RoleCodes.BorderShiftSupervisor);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var name = $"ariva-DMO-2026-10-18-{part}.csv";
        response.Header("Content-Disposition").Should().Be($"attachment; filename={name}; filename*=UTF-8''{name}");
        response.Header("Content-Type").Should().Be("text/csv; charset=utf-8");
        response.Header("X-Content-Type-Options").Should().Be("nosniff");
        response.Header("Cache-Control").Should().Contain("no-store");
    }

    #endregion

    #region Helpers

    private static IArivaHost MainHost(Action<IServiceCollection> replace) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            replace(services);
        }));

    private static async Task<HttpResponseMessage> GetAsync(IArivaHost app, string path, string role)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "stored-file-reader");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, role);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class FixedSiteScope(SiteAccess access) : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(access);
    }

    #endregion
}
