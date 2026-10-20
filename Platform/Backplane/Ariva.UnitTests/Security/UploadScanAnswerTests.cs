using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-092b on Ariva.Api.Main in process, the upload services replaced: every upload endpoint answers the malware scan's
/// refusals the same way, a detection 400 with its fixed text and no scanner verdict 503 with its fixed text and
/// Retry-After: 60, and nothing from the file. The services' own scanning, storing and auditing run against PostgreSQL and a
/// fake clamd in Ariva.IntegrationTests (UploadScanTests).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class UploadScanAnswerTests
{
    #region Fields

    private const string Site = "DMO";

    private static readonly Guid LevelId = Guid.Parse("0199a000-0000-7000-8000-0000000009b1");

    /// <summary>The upload endpoints of Ariva.Api.Main (SteadyUploadTests holds the inventory of the upload policy).</summary>
    public static readonly TheoryData<string, string> Cases = Build();

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Upload_Should_AnswerTheScansRefusalWithItsFixedText_When_TheServiceRefusesTheFile(string endpoint, string error)
    {
        await using var app = MainHost(services => Refuse(services, endpoint, error));
        using var client = app.CreateClient();
        using var request = Request(endpoint);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("detail").GetString().Should().Be(error);
        body.Should().NotContain("EICAR", "nothing from the file is echoed");
        if (error == UploadScanErrors.Malware)
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, endpoint);
            response.Headers.RetryAfter.Should().BeNull();
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, endpoint);
            response.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(60));
            response.Headers.RetryAfter.Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData("FlightSchedules.Preview")]
    [InlineData("FlightSchedules.Import")]
    public async Task FlightSchedules_Should_HandTheServiceTheFileAsAStreamThatCanSeek_When_AFileIsUploaded(string endpoint)
    {
        // The SSIM service scans the file, then reads it again from the same position, and refuses any other stream
        // (SvcFlightSchedules.RequireSeekable): its one caller hands it the buffered form file.
        var seen = new List<(bool CanSeek, long Length)>();
        var schedules = new Mock<ISvcFlightSchedules>(MockBehavior.Strict);
        schedules.Setup(s => s.SiteExistsAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        schedules.Setup(s => s.PreviewAsync(Site, It.IsAny<Stream>(), 7, It.IsAny<CancellationToken>()))
            .Callback((string _, Stream file, int _, CancellationToken _) => seen.Add((file.CanSeek, file.Length)))
            .ReturnsAsync(Result.Error<SsimPreviewViewModel>(UploadScanErrors.Malware));
        schedules.Setup(s => s.ImportAsync(Site, It.IsAny<Stream>(), 7, "token", It.IsAny<CancellationToken>()))
            .Callback((string _, Stream file, int _, string _, CancellationToken _) => seen.Add((file.CanSeek, file.Length)))
            .ReturnsAsync(Result.Error<SsimImportViewModel>(UploadScanErrors.Malware));
        await using var app = MainHost(services => services.Replace(ServiceDescriptor.Scoped(_ => schedules.Object)));
        using var client = app.CreateClient();
        using var request = Request(endpoint);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        seen.Should().Equal([(true, (long)Eicar.Bytes().Length)], "the form file's stream, which can seek, holding the whole file");
    }

    #endregion

    #region Helpers

    private static TheoryData<string, string> Build()
    {
        var data = new TheoryData<string, string>();
        foreach (var endpoint in new[]
                 {
                     "FloorPlans.Upload", "FlightSchedules.Preview", "FlightSchedules.Import", "DeviceImports.Preview", "DeviceImports.Commit",
                     "DeskCodeMappingImports.Preview", "DeskCodeMappingImports.Commit", "SiteConfiguration.Import", "AdvertisingMedia.Upload"
                 })
        {
            data.Add(endpoint, UploadScanErrors.Malware);
            data.Add(endpoint, UploadScanErrors.ScannerUnavailable);
        }

        return data;
    }

    private static IArivaHost MainHost(Action<IServiceCollection> replace) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
            replace(services);
        }));

    /// <summary>The endpoint's service, refusing the upload with <paramref name="error"/> after the checks the controller makes first.</summary>
    private static void Refuse(IServiceCollection services, string endpoint, string error)
    {
        switch (endpoint)
        {
            case "FloorPlans.Upload":
                var plans = new Mock<ISvcFloorPlans>(MockBehavior.Strict);
                plans.Setup(p => p.UploadAsync(LevelId, It.IsAny<FloorPlanUpload>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<FloorPlanViewModel>(error));
                services.Replace(ServiceDescriptor.Scoped(_ => plans.Object));
                break;
            case "FlightSchedules.Preview" or "FlightSchedules.Import":
                var schedules = new Mock<ISvcFlightSchedules>(MockBehavior.Strict);
                schedules.Setup(s => s.SiteExistsAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
                schedules.Setup(s => s.PreviewAsync(Site, It.IsAny<Stream>(), 7, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<SsimPreviewViewModel>(error));
                schedules.Setup(s => s.ImportAsync(Site, It.IsAny<Stream>(), 7, "token", It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<SsimImportViewModel>(error));
                services.Replace(ServiceDescriptor.Scoped(_ => schedules.Object));
                break;
            case "DeviceImports.Preview" or "DeviceImports.Commit":
                var devices = new Mock<ISvcDeviceImports>(MockBehavior.Strict);
                devices.Setup(d => d.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
                devices.Setup(d => d.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<DeviceImportPreviewViewModel>(error));
                devices.Setup(d => d.CommitAsync(Site, It.IsAny<Func<Stream>>(), "token", It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<DeviceImportCommitViewModel>(error));
                services.Replace(ServiceDescriptor.Scoped(_ => devices.Object));
                break;
            case "DeskCodeMappingImports.Preview" or "DeskCodeMappingImports.Commit":
                var mappings = new Mock<ISvcDeskCodeMappingImports>(MockBehavior.Strict);
                mappings.Setup(m => m.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
                mappings.Setup(m => m.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<DeskCodeMappingImportPreviewViewModel>(error));
                mappings.Setup(m => m.CommitAsync(Site, It.IsAny<Func<Stream>>(), "token", It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<DeskCodeMappingImportCommitViewModel>(error));
                services.Replace(ServiceDescriptor.Scoped(_ => mappings.Object));
                break;
            case "SiteConfiguration.Import":
                var configuration = new Mock<ISvcSiteConfiguration>(MockBehavior.Strict);
                configuration.Setup(c => c.ImportAsync(Site, It.IsAny<ImportSiteConfigurationRequest>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<SiteConfigurationImportViewModel>(error));
                services.Replace(ServiceDescriptor.Scoped(_ => configuration.Object));
                break;
            case "AdvertisingMedia.Upload":
                var policies = new Mock<ISvcAdvertisingPolicies>(MockBehavior.Strict);
                policies.Setup(p => p.AccessAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(AdvertisingAccess.Allowed);
                var media = new Mock<ISvcAdvertisingMedia>(MockBehavior.Strict);
                media.Setup(m => m.UploadAsync(Site, It.IsAny<AdvertisingMediaUpload>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<AdvertisingMediaViewModel>(error));
                services.Replace(ServiceDescriptor.Scoped(_ => policies.Object));
                services.Replace(ServiceDescriptor.Scoped(_ => media.Object));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not an upload endpoint.");
        }
    }

    /// <summary>The endpoint's request as an administrator with a recent second factor would send it, the file being the EICAR test file.</summary>
    private static HttpRequestMessage Request(string endpoint)
    {
        var (path, fields) = endpoint switch
        {
            "FloorPlans.Upload" => ($"/api/v1/admin/levels/{LevelId}/floor-plan", new Dictionary<string, string> { ["metresPerPixel"] = "0.05", ["originX"] = "0", ["originY"] = "0" }),
            "FlightSchedules.Preview" => ($"/api/v1/admin/sites/{Site}/flight-schedules/preview", new Dictionary<string, string> { ["horizonDays"] = "7" }),
            "FlightSchedules.Import" => ($"/api/v1/admin/sites/{Site}/flight-schedules/import", new Dictionary<string, string> { ["horizonDays"] = "7", ["previewToken"] = "token" }),
            "DeviceImports.Preview" => ($"/api/v1/admin/sites/{Site}/device-imports/preview", new Dictionary<string, string>()),
            "DeviceImports.Commit" => ($"/api/v1/admin/sites/{Site}/device-imports/commit", new Dictionary<string, string> { ["previewToken"] = "token" }),
            "DeskCodeMappingImports.Preview" => ($"/api/v1/admin/sites/{Site}/desk-code-mapping-imports/preview", new Dictionary<string, string>()),
            "DeskCodeMappingImports.Commit" => ($"/api/v1/admin/sites/{Site}/desk-code-mapping-imports/commit", new Dictionary<string, string> { ["previewToken"] = "token" }),
            "AdvertisingMedia.Upload" => ($"/api/v1/admin/sites/{Site}/advertising/media", new Dictionary<string, string>
            {
                ["advertiser"] = "Gulf Air", ["campaign"] = "Winter sun", ["startsOn"] = "2026-10-20", ["endsOn"] = "2026-10-31"
            }),
            _ => ($"/api/v1/admin/sites/{Site}/configuration", (Dictionary<string, string>)null)
        };

        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "upload-scan");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);
        request.Headers.Add(TestAuthenticationHandler.MethodsHeader, "pwd,otp");
        request.Headers.Add(TestAuthenticationHandler.AuthTimeHeader, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        if (fields is null)
        {
            var bundle = new
            {
                formatVersion = 1, sourceSite = Site, exportedAtUtc = "2026-10-20T08:00:00Z",
                manifest = new Dictionary<string, string> { ["floorPlanFile.1"] = new string('a', 64) },
                parts = new Dictionary<string, string> { ["floorPlanFile.1"] = Convert.ToBase64String(Eicar.Bytes()) }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(bundle), Encoding.UTF8, "application/json");
            return request;
        }

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Eicar.Bytes());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "upload.bin");
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);
        request.Content = form;
        return request;
    }

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }

    #endregion
}
