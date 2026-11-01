using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Logging;
using Ariva.Api.Main.Controllers.AdminArea.Uploads;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Topology;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-203 (ASVS 5.0 V16.3.3; CWE-778, CWE-532) on Ariva.Api.Main in process, the upload services replaced: an upload refused by
/// its service or its controller (a file the service refuses, a target not found, a stale preview token, a file over its
/// limit) writes exactly one security event, <c>SecurityEvent.UploadRefused</c> (9134), naming the caller, the upload's kind,
/// the endpoint's route pattern, the status and a fixed reason for it, and never the file, its name or the service's text. One
/// test per upload endpoint. No such event is written for a taken upload, for a refusal another security event records (an
/// invalid model, a missing permission, the upload allowance), or for a request that is no upload (the site configuration
/// export). The throttle is SecurityEventThrottleTests'.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class UploadRefusalEventTests : IDisposable
{
    #region Fields

    private const string Site = "DMO";

    /// <summary>In the file's name and content and in the service's refusal text; never in a log line.</summary>
    private const string Canary = "canary-203f71";

    private static readonly Guid LevelId = Guid.Parse("0199a000-0000-7000-8000-000000000203");

    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-upload-refusals-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Theory data

    /// <summary>Every upload endpoint (every endpoint of the upload policy that takes a file or a bundle) with its kind.</summary>
    public static TheoryData<string, string> Uploads => new()
    {
        { "FloorPlans.Upload", UploadLimitAttribute.FloorPlan },
        { "FlightSchedules.Preview", UploadLimitAttribute.FlightSchedulePreview },
        { "FlightSchedules.Import", UploadLimitAttribute.FlightScheduleImport },
        { "SiteConfiguration.Import", UploadLimitAttribute.SiteConfigurationImport },
        { "AdvertisingMedia.Upload", UploadLimitAttribute.AdvertisingMedia },
        { "DeviceImports.Preview", UploadLimitAttribute.CsvPreview },
        { "DeviceImports.Commit", UploadLimitAttribute.CsvCommit },
        { "DeskCodeMappingImports.Preview", UploadLimitAttribute.CsvPreview },
        { "DeskCodeMappingImports.Commit", UploadLimitAttribute.CsvCommit }
    };

    #endregion

    #region Lifetime

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task Refusal_Should_WriteOneUploadRefusedEventWithWhoWhichUploadAndWhy_When_TheUploadIsRefused(string endpoint, string kind)
    {
        var log = Path.Combine(_folder, "main.log");
        HttpStatusCode status;
        await using (var app = MainHost(new Services(), log))
        {
            using var client = app.CreateClient();
            using var answer = await client.SendAsync(Request(endpoint, "uploader"), Ct);
            status = answer.StatusCode;
        }

        SecurityEvents.UploadRefusedReasonOf((int)status).Should().NotBeNull($"{endpoint} is refused with a status the event records, not {status}");
        var security = RequestEvents(log);
        security.Should().ContainSingle($"{endpoint} refused is one refusal: one security event");
        var entry = security[0];
        entry.GetProperty("Level").GetString().Should().Be("Warning");
        var properties = entry.GetProperty("Properties");
        properties.GetProperty("EventId").GetProperty("Id").GetInt32().Should().Be(SecurityEvents.UploadRefusedId);
        properties.GetProperty("EventId").GetProperty("Name").GetString().Should().Be(SecurityEvents.UploadRefusedName);
        properties.GetProperty("UploadKind").GetString().Should().Be(kind);
        properties.GetProperty("Status").GetInt32().Should().Be((int)status);
        properties.GetProperty("Reason").GetString().Should().Be(SecurityEvents.UploadRefusedReasonOf((int)status));
        properties.GetProperty("Method").GetString().Should().Be("POST");
        properties.GetProperty("Endpoint").GetString().Should().StartWith("api/v1/admin/").And.NotContain(Site, "the route pattern, never the path");
        properties.TryGetProperty(RequestLogContext.CallerUserIdProperty, out _).Should().BeTrue("who: the request's caller");
        properties.TryGetProperty(RequestLogContext.CallerAddressProperty, out _).Should().BeTrue();
        File.ReadAllText(log).Should().NotContain(Canary, "neither the file, its name nor the service's text is logged");
    }

    [Theory]
    [InlineData(404, "FloorPlans.Upload")]
    [InlineData(409, "DeviceImports.Commit")]
    [InlineData(413, "DeskCodeMappingImports.Preview")]
    [InlineData(415, "SiteConfiguration.Import")]
    public async Task Refusal_Should_NameTheStatusAndItsReason_When_TheUploadIsRefusedSo(int status, string endpoint)
    {
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(new Services(), log))
        {
            using var client = app.CreateClient();
            // 413: a CSV file of 1 MB and one byte. 415: a bundle sent as plain text, refused by MVC after the import's filters marked it.
            using var request = status switch
            {
                413 => Request(endpoint, "uploader", CsvImportLimits.MaxFileBytes + 1),
                415 => Signed(HttpMethod.Post, Shape(endpoint).Path, "uploader"),
                _ => Request(endpoint, "uploader")
            };
            if (status == 415)
                request.Content = new StringContent(Canary, Encoding.UTF8, "text/plain");
            using var answer = await client.SendAsync(request, Ct);
            ((int)answer.StatusCode).Should().Be(status);
        }

        var properties = RequestEvents(log).Should().ContainSingle().Subject.GetProperty("Properties");
        properties.GetProperty("Status").GetInt32().Should().Be(status);
        properties.GetProperty("Reason").GetString().Should().Be(status switch
        {
            404 => SecurityEvents.UploadTargetNotFound,
            409 => SecurityEvents.UploadConflict,
            413 => SecurityEvents.UploadTooLarge,
            _ => SecurityEvents.UploadUnsupportedType
        });
        File.ReadAllText(log).Should().NotContain(Canary, "neither the body nor the file is logged");
    }

    [Fact]
    public async Task Upload_Should_WriteNoSecurityEvent_When_ItIsTaken()
    {
        var log = Path.Combine(_folder, "main.log");
        var services = new Services();
        services.Plans.Setup(p => p.UploadAsync(LevelId, It.IsAny<FloorPlanUpload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<FloorPlanViewModel>(new FloorPlanViewModel(Guid.CreateVersion7(), LevelId, "image/png", 10, new string('a', 64), "plan.png",
                2, 2, 0.05, 0, 0, Site, DateTime.UtcNow)));
        await using (var app = MainHost(services, log))
        {
            using var client = app.CreateClient();
            using var answer = await client.SendAsync(Request("FloorPlans.Upload", "uploader"), Ct);
            answer.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        RequestEvents(log).Should().BeEmpty();
    }

    [Fact]
    public async Task Refusal_Should_WriteOnlyItsOwnEvent_When_AnotherSecurityEventRecordsIt()
    {
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(new Services(), log, uploadsPerWindow: 1))
        {
            using var client = app.CreateClient();
            // An invalid model: horizonDays does not bind (9117).
            using (var invalid = await client.SendAsync(Request("FlightSchedules.Preview", "first", fields: new() { ["horizonDays"] = "soon" }), Ct))
                invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            // No permission: the authorization middleware's 403 (9114), before the upload's filter.
            using (var forbidden = await client.SendAsync(Request("FloorPlans.Upload", "second", roles: Ariva.Core.RoleCodes.BorderShiftSupervisor), Ct))
                forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            // The allowance of one: the first upload is refused by its service (9134), the second by the limit (9129).
            using (var first = await client.SendAsync(Request("FloorPlans.Upload", "third"), Ct))
                first.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using (var limited = await client.SendAsync(Request("FloorPlans.Upload", "third"), Ct))
                limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        RequestEvents(log).Select(e => e.GetProperty("Properties").GetProperty("EventId").GetProperty("Id").GetInt32()).Should().Equal(
            [SecurityEvents.InvalidModelRefusedId, SecurityEvents.AuthorizationForbiddenId, SecurityEvents.UploadRefusedId, SecurityEvents.RequestRefusedId],
            "one event a refusal, each its own kind");
    }

    [Fact]
    public async Task Refusal_Should_WriteNoUploadEvent_When_TheRequestIsNoUpload()
    {
        // The site configuration export shares the upload policy, takes no file and answers 404 here (its service refuses).
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(new Services(), log))
        {
            using var client = app.CreateClient();
            using var answer = await client.SendAsync(Signed(HttpMethod.Get, $"/api/v1/admin/sites/{Site}/configuration", "exporter"), Ct);
            answer.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        RequestEvents(log).Should().BeEmpty("an export is no upload");
        UploadLimitAttribute.IsUpload(UploadLimitAttribute.SiteConfigurationExport).Should().BeFalse();
    }

    [Theory]
    [InlineData(400, SecurityEvents.UploadInvalid)]
    [InlineData(404, SecurityEvents.UploadTargetNotFound)]
    [InlineData(408, SecurityEvents.UploadTooSlow)]
    [InlineData(409, SecurityEvents.UploadConflict)]
    [InlineData(413, SecurityEvents.UploadTooLarge)]
    [InlineData(415, SecurityEvents.UploadUnsupportedType)]
    [InlineData(422, SecurityEvents.UploadUnprocessable)]
    [InlineData(200, null)]
    [InlineData(201, null)]
    [InlineData(401, null)]
    [InlineData(403, null)]
    [InlineData(429, null)]
    [InlineData(500, null)]
    [InlineData(503, null)]
    public void UploadRefusedReasonOf_Should_NameAFixedReason_When_TheStatusIsAnUploadRefusal(int status, string reason) =>
        SecurityEvents.UploadRefusedReasonOf(status).Should().Be(reason, "401, 403 and 429 are RequestRefused's, 5xx no refusal of the upload");

    #endregion

    #region Helpers

    private static IArivaHost MainHost(Services services, string log, int uploadsPerWindow = 100) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            builder.UseSetting(ArivaLogging.FileSetting, log);
            builder.UseSetting("Security:RateLimiting:Uploads:PermitLimit", uploadsPerWindow.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Security:RateLimiting:Uploads:WindowSeconds", "600");
            builder.UseSetting("Security:RateLimiting:CsvImports:PermitLimit", "100");
            builder.ConfigureTestServices(collection =>
            {
                TestAuthenticationHandler.Register(collection);
                FakeAdministration.Register(collection);
                collection.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
                services.Register(collection);
            });
        });

    /// <summary>The upload as an administrator with a recent second factor would send it, its file named and filled with the canary.</summary>
    private static HttpRequestMessage Request(string endpoint, string user, int fileBytes = 0, Dictionary<string, string> fields = null,
        string roles = Ariva.Core.RoleCodes.SystemAdministrator)
    {
        var (path, defaults) = Shape(endpoint);
        var request = Signed(HttpMethod.Post, path, user, roles);
        if (defaults is null)
        {
            var bundle = new
            {
                formatVersion = 1, sourceSite = Site, exportedAtUtc = "2026-10-20T08:00:00Z",
                manifest = new Dictionary<string, string> { ["topology"] = new string('0', 64) },
                parts = new Dictionary<string, string> { ["topology"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary)) }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(bundle), Encoding.UTF8, "application/json");
            return request;
        }

        var form = new MultipartFormDataContent();
        var content = Encoding.UTF8.GetBytes($"{Canary}\r\n");
        if (fileBytes > content.Length)
            content = [.. content, .. Enumerable.Repeat((byte)'x', fileBytes - content.Length)];
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", $"{Canary}.bin");
        foreach (var (name, value) in fields ?? defaults)
            form.Add(new StringContent(value), name);
        request.Content = form;
        return request;
    }

    private static (string Path, Dictionary<string, string> Fields) Shape(string endpoint) => endpoint switch
    {
        "FloorPlans.Upload" => ($"/api/v1/admin/levels/{LevelId}/floor-plan", new() { ["metresPerPixel"] = "0.05", ["originX"] = "0", ["originY"] = "0" }),
        "FlightSchedules.Preview" => ($"/api/v1/admin/sites/{Site}/flight-schedules/preview", new() { ["horizonDays"] = "7" }),
        "FlightSchedules.Import" => ($"/api/v1/admin/sites/{Site}/flight-schedules/import", new() { ["horizonDays"] = "7", ["previewToken"] = "token" }),
        "SiteConfiguration.Import" => ($"/api/v1/admin/sites/{Site}/configuration", null),
        "AdvertisingMedia.Upload" => ($"/api/v1/admin/sites/{Site}/advertising/media", new()
        {
            ["advertiser"] = "Gulf Air", ["campaign"] = "Winter sun", ["startsOn"] = "2026-10-20", ["endsOn"] = "2026-10-31"
        }),
        "DeviceImports.Preview" => ($"/api/v1/admin/sites/{Site}/device-imports/preview", new()),
        "DeviceImports.Commit" => ($"/api/v1/admin/sites/{Site}/device-imports/commit", new() { ["previewToken"] = "token" }),
        "DeskCodeMappingImports.Preview" => ($"/api/v1/admin/sites/{Site}/desk-code-mapping-imports/preview", new()),
        "DeskCodeMappingImports.Commit" => ($"/api/v1/admin/sites/{Site}/desk-code-mapping-imports/commit", new() { ["previewToken"] = "token" }),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not an upload endpoint of these tests.")
    };

    private static HttpRequestMessage Signed(HttpMethod method, string path, string user, string roles = Ariva.Core.RoleCodes.SystemAdministrator)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, user);
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthenticationHandler.MethodsHeader, "pwd,otp");
        request.Headers.Add(TestAuthenticationHandler.AuthTimeHeader, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return request;
    }

    /// <summary>The request-level security events of the host's JSON lines, in order.</summary>
    private static List<JsonElement> RequestEvents(string log)
    {
        File.Exists(log).Should().BeTrue("the host writes its JSON lines to LogFile:Path in vm-local");
        return File.ReadAllLines(log).Where(line => line.Length > 0).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(line => line.GetProperty("Properties").TryGetProperty("EventId", out var id) && id.TryGetProperty("Id", out var value) &&
                         SecurityEvents.RequestEventIds.Contains(value.GetInt32())).ToList();
    }

    /// <summary>Every upload service, each refusing what reaches it with a text that holds the canary.</summary>
    private sealed class Services
    {
        public Mock<ISvcFloorPlans> Plans { get; } = new(MockBehavior.Strict);
        public Mock<ISvcFlightSchedules> Schedules { get; } = new(MockBehavior.Strict);
        public Mock<ISvcSiteConfiguration> Configuration { get; } = new(MockBehavior.Strict);
        public Mock<ISvcAdvertisingPolicies> Policies { get; } = new(MockBehavior.Strict);
        public Mock<ISvcAdvertisingMedia> Media { get; } = new(MockBehavior.Strict);
        public Mock<ISvcDeviceImports> Devices { get; } = new(MockBehavior.Strict);
        public Mock<ISvcDeskCodeMappingImports> Mappings { get; } = new(MockBehavior.Strict);

        public Services()
        {
            Plans.Setup(p => p.UploadAsync(LevelId, It.IsAny<FloorPlanUpload>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<FloorPlanViewModel>(TopologyErrors.NotFound));
            Schedules.Setup(s => s.SiteExistsAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Schedules.Setup(s => s.PreviewAsync(Site, It.IsAny<Stream>(), 7, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<SsimPreviewViewModel>($"Line 1 is not an SSIM record: {Canary}"));
            Schedules.Setup(s => s.ImportAsync(Site, It.IsAny<Stream>(), 7, "token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<SsimImportViewModel>(FlightScheduleErrors.NotThePreview));
            Configuration.Setup(c => c.ExportAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<SiteConfigurationExportViewModel>(TopologyErrors.NotFound));
            Configuration.Setup(c => c.ImportAsync(Site, It.IsAny<ImportSiteConfigurationRequest>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<SiteConfigurationImportViewModel>(SiteConfigurationErrors.HashMismatch));
            Policies.Setup(p => p.AccessAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(AdvertisingAccess.Allowed);
            Media.Setup(m => m.UploadAsync(Site, It.IsAny<AdvertisingMediaUpload>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.UnsupportedFile));
            Devices.Setup(d => d.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Devices.Setup(d => d.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<DeviceImportPreviewViewModel>(CsvImportErrors.NoRows));
            Devices.Setup(d => d.CommitAsync(Site, It.IsAny<Func<Stream>>(), "token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<DeviceImportCommitViewModel>(CsvImportErrors.NotThePreview));
            Mappings.Setup(m => m.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Mappings.Setup(m => m.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<DeskCodeMappingImportPreviewViewModel>(CsvImportErrors.NoRows));
            Mappings.Setup(m => m.CommitAsync(Site, It.IsAny<Func<Stream>>(), "token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<DeskCodeMappingImportCommitViewModel>(CsvImportErrors.NotThePreview));
        }

        public void Register(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Scoped(_ => Plans.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Schedules.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Configuration.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Policies.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Media.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Devices.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Mappings.Object));
        }
    }

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }

    #endregion
}
