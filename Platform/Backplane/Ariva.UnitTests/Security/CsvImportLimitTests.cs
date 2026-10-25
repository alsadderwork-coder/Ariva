using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Logging;
using Ariva.Api.Main.Controllers.AdminArea.Imports;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Imports;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-155 (CWE-400, CWE-770) on Ariva.Api.Main in process, the import services replaced: a user's CSV import previews, and
/// apart from them their commits, are counted per user in a fixed window (here 2 in 10 minutes), one allowance for the device
/// and the desk code mapping imports. Past it a request answers 429 with Retry-After and the fixed text before the file is
/// read (a malformed body is refused 429, not the upload filter's 400) and never reaches the service; another user, and the
/// same user's other step, are not held back. A refusal is recorded as a refused request (LimitReached); a caller the
/// authorization refuses is never counted; an import that waited out the site's lock answers 409 with its fixed text; every
/// action of the two import controllers carries the limit.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class CsvImportLimitTests : IDisposable
{
    #region Fields

    private const string Site = "DMO";
    private const string DevicePreview = "/api/v1/admin/sites/DMO/device-imports/preview";
    private const string DeviceCommit = "/api/v1/admin/sites/DMO/device-imports/commit";
    private const string MappingPreview = "/api/v1/admin/sites/DMO/desk-code-mapping-imports/preview";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-csv-limit-").FullName;

    #endregion

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    #region Tests

    [Fact]
    public async Task Preview_Should_Answer429WithRetryAfterBeforeTheService_When_TheUserPassesTheirLimit()
    {
        var (devices, mappings) = (Devices(), Mappings());
        await using var app = MainHost(devices, mappings);
        using var client = app.CreateClient();

        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            using var preview = await client.SendAsync(Request(DevicePreview, "planner"), Ct);
            answers.Add(preview.StatusCode);
        }

        using var refused = await client.SendAsync(Request(DevicePreview, "planner"), Ct);
        using var mappingRefused = await client.SendAsync(Request(MappingPreview, "planner"), Ct);

        answers.Should().Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest], "the first two reach the service, which refuses the empty file");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter?.Delta.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)))
            problem.RootElement.GetProperty("detail").GetString().Should().Be(CsvImportErrors.TooManyImports);
        mappingRefused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "one allowance for both imports' previews");
        devices.Verify(d => d.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        mappings.Verify(m => m.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Preview_Should_RefuseBeforeTheBodyIsRead_When_TheUserIsPastTheirLimit()
    {
        await using var app = MainHost(Devices(), Mappings());
        using var client = app.CreateClient();
        using var malformed = Malformed(DevicePreview, "reader");
        using var unread = await client.SendAsync(malformed, Ct);
        for (var i = 0; i < 1; i++)
            (await client.SendAsync(Request(DevicePreview, "reader"), Ct)).Dispose();

        using var late = Malformed(DevicePreview, "reader");
        using var refused = await client.SendAsync(late, Ct);

        unread.StatusCode.Should().Be(HttpStatusCode.BadRequest, "within the limit the upload filter reads the body and refuses it");
        using (var problem = JsonDocument.Parse(await unread.Content.ReadAsStringAsync(Ct)))
            problem.RootElement.GetProperty("detail").GetString().Should().Be(SteadyUploadAttribute.Unreadable);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "past the limit the request is refused before its body is read");
    }

    [Fact]
    public async Task Commit_Should_BeCountedApartFromPreviewsAndPerUser_When_OneIsPastItsLimit()
    {
        var devices = Devices();
        await using var app = MainHost(devices, Mappings());
        using var client = app.CreateClient();
        for (var i = 0; i < 2; i++)
            (await client.SendAsync(Request(DevicePreview, "planner"), Ct)).Dispose();

        using var previewRefused = await client.SendAsync(Request(DevicePreview, "planner"), Ct);
        using var commit = await client.SendAsync(Request(DeviceCommit, "planner"), Ct);
        using var otherUser = await client.SendAsync(Request(DevicePreview, "second-planner"), Ct);

        previewRefused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        commit.StatusCode.Should().Be(HttpStatusCode.Conflict, "the commit has its own allowance and reaches the service");
        otherUser.StatusCode.Should().Be(HttpStatusCode.BadRequest, "another user has their own allowance");
        devices.Verify(d => d.CommitAsync(Site, It.IsAny<Func<Stream>>(), "token", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Preview_Should_BeRecordedAsARefusedRequest_When_TheUserIsPastTheirLimit()
    {
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(Devices(), Mappings(), log))
        {
            using var client = app.CreateClient();
            for (var i = 0; i < 3; i++)
                (await client.SendAsync(Request(MappingPreview, "planner"), Ct)).Dispose();
        }

        var refused = File.ReadAllLines(log).Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(line => line.GetProperty("Properties").TryGetProperty("EventId", out var id) && id.GetProperty("Id").GetInt32() == SecurityEvents.RequestRefusedId)
            .Select(line => line.GetProperty("Properties")).ToList();
        refused.Should().ContainSingle("only the third preview is refused");
        refused[0].GetProperty("Status").GetInt32().Should().Be(429);
        refused[0].GetProperty("Reason").GetString().Should().Be(SecurityEvents.LimitReached);
        File.ReadAllText(log).Should().NotContain("name:planner", "the limiter's key is never logged");
    }

    [Fact]
    public async Task Preview_Should_NotCountACallerTheAuthorizationRefuses_When_TheSecondFactorIsMissing()
    {
        // The device import is a critical action: without a recent second factor authorization answers 401 before any filter, so
        // three refused previews spend none of the user's allowance of two.
        var devices = Devices();
        await using var app = MainHost(devices, Mappings());
        using var client = app.CreateClient();

        var refused = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = Request(DevicePreview, "planner");
            request.Headers.Remove(TestAuthenticationHandler.MethodsHeader);
            request.Headers.Remove(TestAuthenticationHandler.AuthTimeHeader);
            using var answer = await client.SendAsync(request, Ct);
            refused.Add(answer.StatusCode);
        }

        using var stepped = await client.SendAsync(Request(DevicePreview, "planner"), Ct);

        refused.Should().Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized]);
        stepped.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the allowance is whole: the preview reaches the service");
        devices.Verify(d => d.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Preview_Should_Answer409WithTheFixedText_When_TheSiteLockWasHeldLongerThanTheWait()
    {
        var mappings = Mappings();
        mappings.Setup(m => m.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Error<DeskCodeMappingImportPreviewViewModel>(CsvImportErrors.SiteBusy));
        await using var app = MainHost(Devices(), mappings);
        using var client = app.CreateClient();

        using var busy = await client.SendAsync(Request(MappingPreview, "planner"), Ct);

        busy.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await busy.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("title").GetString().Should().Be("Conflict");
        problem.RootElement.GetProperty("detail").GetString().Should().Be(CsvImportErrors.SiteBusy);
    }

    [Fact]
    public void ImportActions_Should_EachCarryTheLimit_When_Inspected()
    {
        var actions = new[] { typeof(DeviceImportsController), typeof(DeskCodeMappingImportsController) }
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).ToList();

        actions.Should().HaveCount(4);
        actions.Select(a => (a.Name, a.GetCustomAttribute<CsvImportLimitAttribute>()?.Step))
            .Should().BeEquivalentTo(new[] { ("Preview", "preview"), ("Commit", "commit"), ("Preview", "preview"), ("Commit", "commit") });
    }

    [Fact]
    public void CallerOf_Should_PreferTheUserIdAndRefuseACallerWithNeither_When_Asked()
    {
        var id = Guid.Parse("0199a000-0000-7000-8000-000000000155");

        CsvImportLimitAttribute.CallerOf(User(id, "planner")).Should().Be("id:" + id.ToString("N", CultureInfo.InvariantCulture));
        CsvImportLimitAttribute.CallerOf(User(null, "planner")).Should().Be("name:planner", "only the test scheme's principals have no id");
        CsvImportLimitAttribute.CallerOf(User(null, null)).Should().BeNull();
        CsvImportLimitAttribute.CallerOf(User(null, "")).Should().BeNull();
        CsvImportLimitAttribute.CallerOf(null).Should().BeNull();
    }

    #endregion

    #region Helpers

    private static IArivaHost MainHost(Mock<ISvcDeviceImports> devices, Mock<ISvcDeskCodeMappingImports> mappings, string log = null) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            if (log is not null)
                builder.UseSetting(ArivaLogging.FileSetting, log);
            builder.UseSetting("Security:RateLimiting:CsvImports:PermitLimit", "2");
            builder.UseSetting("Security:RateLimiting:CsvImports:WindowSeconds", "600");
            builder.ConfigureTestServices(services =>
            {
                TestAuthenticationHandler.Register(services);
                FakeAdministration.Register(services);
                services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
                services.Replace(ServiceDescriptor.Scoped(_ => devices.Object));
                services.Replace(ServiceDescriptor.Scoped(_ => mappings.Object));
            });
        });

    private static Mock<ISvcDeviceImports> Devices()
    {
        var devices = new Mock<ISvcDeviceImports>(MockBehavior.Strict);
        devices.Setup(d => d.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        devices.Setup(d => d.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<DeviceImportPreviewViewModel>(CsvImportErrors.NoRows));
        devices.Setup(d => d.CommitAsync(Site, It.IsAny<Func<Stream>>(), "token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Error<DeviceImportCommitViewModel>(CsvImportErrors.NotThePreview));
        return devices;
    }

    private static Mock<ISvcDeskCodeMappingImports> Mappings()
    {
        var mappings = new Mock<ISvcDeskCodeMappingImports>(MockBehavior.Strict);
        mappings.Setup(m => m.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mappings.Setup(m => m.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Error<DeskCodeMappingImportPreviewViewModel>(CsvImportErrors.NoRows));
        return mappings;
    }

    /// <summary>A preview or commit as an administrator with a recent second factor would send it (registering devices is critical).</summary>
    private static HttpRequestMessage Request(string path, string user)
    {
        var request = Signed(path, user);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("code\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "devices.csv");
        if (path.EndsWith("/commit", StringComparison.Ordinal))
            form.Add(new StringContent("token"), "previewToken");
        request.Content = form;
        return request;
    }

    /// <summary>A multipart body cut short: the upload filter refuses it 400 once it reads it.</summary>
    private static HttpRequestMessage Malformed(string path, string user)
    {
        var request = Signed(path, user);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("--cut\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.csv\"\r\n\r\ncode"));
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=cut");
        return request;
    }

    private static HttpRequestMessage Signed(string path, string user)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, user);
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);
        request.Headers.Add(TestAuthenticationHandler.MethodsHeader, "pwd,otp");
        request.Headers.Add(TestAuthenticationHandler.AuthTimeHeader, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return request;
    }

    private static ICurrentUser User(Guid? id, string name)
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(u => u.Id).Returns(id);
        user.SetupGet(u => u.UserName).Returns(name);
        return user.Object;
    }

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }

    #endregion
}
