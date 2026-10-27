using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Settings;
using Ariva.Api.Main.Controllers.AdminArea.Uploads;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Topology;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-250 (CWE-400, CWE-770) on Ariva.Api.Main in process, the upload services replaced: every endpoint of the <c>upload</c>
/// policy counts its caller's requests per user and kind in a sliding window (here 2 in 10 minutes), after authorization and
/// before the body is read. Past it a request answers 429 with Retry-After and the fixed text and never reaches the service
/// (a malformed body is refused 429, not 400); another user, another kind and the CSV imports (their own window) are not held
/// back; a caller the authorization refuses is never counted; every endpoint of the policy carries the limit with its kind.
/// The CSV imports' own cases are CsvImportLimitTests'. ARV-265: each user's share of the host's upload places (one running or
/// waiting by default), the host's one permit and two waiting places taken after authorization, a refusal of the host spending none
/// of the user's allowance, and a caller past their allowance taking no place; on a real Kestrel in <see cref="SteadyUploadTests"/>.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class UploadLimitTests
{
    #region Fields

    private const string Site = "DMO";

    private static readonly Guid LevelId = Guid.Parse("0199a000-0000-7000-8000-000000000250");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every endpoint of the upload policy but the CSV imports', with its kind.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["FloorPlans.Upload"] = UploadLimitAttribute.FloorPlan,
        ["FlightSchedules.Preview"] = UploadLimitAttribute.FlightSchedulePreview,
        ["FlightSchedules.Import"] = UploadLimitAttribute.FlightScheduleImport,
        ["SiteConfiguration.Export"] = UploadLimitAttribute.SiteConfigurationExport,
        ["SiteConfiguration.Import"] = UploadLimitAttribute.SiteConfigurationImport,
        ["AdvertisingMedia.Upload"] = UploadLimitAttribute.AdvertisingMedia
    };

    public static TheoryData<string> Endpoints => [.. Kinds.Keys];

    /// <summary>The endpoints that take a body (the export is a GET).</summary>
    public static TheoryData<string> Bodies => [.. Kinds.Keys.Where(e => e != "SiteConfiguration.Export")];

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Upload_Should_Answer429WithRetryAfterBeforeTheService_When_TheUserPassesTheirLimit(string endpoint)
    {
        var services = new Services();
        await using var app = MainHost(services);
        using var client = app.CreateClient();

        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            using var allowed = await client.SendAsync(Request(endpoint, "planner"), Ct);
            answers.Add(allowed.StatusCode);
        }

        using var refused = await client.SendAsync(Request(endpoint, "planner"), Ct);
        using var otherUser = await client.SendAsync(Request(endpoint, "second-planner"), Ct);

        answers.Should().NotContain(HttpStatusCode.TooManyRequests, "the first two reach the service");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, endpoint);
        refused.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(610), "the window and one segment, by which every permit counted now is free");
        refused.Headers.RetryAfter.Should().NotBeNull();
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)))
        {
            problem.RootElement.GetProperty("title").GetString().Should().Be("Too many requests");
            problem.RootElement.GetProperty("detail").GetString().Should().Be(UploadLimitAttribute.TooManyUploads);
        }

        otherUser.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "another user has their own allowance");
        services.Calls(endpoint).Should().Be(3, "the two allowed and the other user's request reach the service, the refused one never");
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task Upload_Should_RefuseBeforeTheBodyIsRead_When_TheUserIsPastTheirLimit(string endpoint)
    {
        // A form cut short is refused by the steady upload's read (the advertisement upload's own read for its form); the bundle's
        // JSON, which the steady upload buffers without parsing, by model binding. Past the limit both are refused 429 before either.
        var services = new Services();
        await using var app = MainHost(services);
        using var client = app.CreateClient();
        using var unread = await client.SendAsync(Malformed(endpoint, "reader"), Ct);
        (await client.SendAsync(Request(endpoint, "reader"), Ct)).Dispose();

        using var refused = await client.SendAsync(Malformed(endpoint, "reader"), Ct);

        unread.StatusCode.Should().Be(HttpStatusCode.BadRequest, "within the limit the body is read and refused");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "past the limit the request is refused before its body is read");
        services.Calls(endpoint).Should().Be(1, "only the well formed request reaches the service");
    }

    [Fact]
    public async Task Upload_Should_CountEachKindApart_When_OneKindIsPastItsLimit()
    {
        var services = new Services();
        await using var app = MainHost(services, csvImports: 3);
        using var client = app.CreateClient();
        for (var i = 0; i < 2; i++)
            (await client.SendAsync(Request("FlightSchedules.Preview", "planner"), Ct)).Dispose();

        using var previewRefused = await client.SendAsync(Request("FlightSchedules.Preview", "planner"), Ct);
        using var import = await client.SendAsync(Request("FlightSchedules.Import", "planner"), Ct);
        using var floorPlan = await client.SendAsync(Request("FloorPlans.Upload", "planner"), Ct);
        var csv = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var preview = await client.SendAsync(Request("DeviceImports.Preview", "planner"), Ct);
            csv.Add(preview.StatusCode);
        }

        previewRefused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        import.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "the import has its own allowance");
        floorPlan.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "so has the floor plan");
        csv.Should().NotContain(HttpStatusCode.TooManyRequests, "the CSV imports are counted in their own window (3 here)");
        services.Calls("FlightSchedules.Import").Should().Be(1);
        services.Calls("FloorPlans.Upload").Should().Be(1);
        services.Calls("DeviceImports.Preview").Should().Be(3);
    }

    [Fact]
    public async Task Import_Should_NotCountACallerTheAuthorizationRefuses_When_TheSecondFactorIsMissing()
    {
        // The site configuration import is a critical action: without a recent second factor authorization answers 401 before any
        // filter, so three refused imports spend none of the user's allowance of two.
        var services = new Services();
        await using var app = MainHost(services);
        using var client = app.CreateClient();

        var refused = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = Request("SiteConfiguration.Import", "planner");
            request.Headers.Remove(TestAuthenticationHandler.MethodsHeader);
            request.Headers.Remove(TestAuthenticationHandler.AuthTimeHeader);
            using var answer = await client.SendAsync(request, Ct);
            refused.Add(answer.StatusCode);
        }

        using var stepped = await client.SendAsync(Request("SiteConfiguration.Import", "planner"), Ct);
        using var again = await client.SendAsync(Request("SiteConfiguration.Import", "planner"), Ct);

        refused.Should().Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized]);
        stepped.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "the allowance is whole");
        again.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        services.Calls("SiteConfiguration.Import").Should().Be(2);
    }

    [Fact]
    public async Task Upload_Should_NotCountACallerTheAuthorizationRefuses_When_TheyLackThePermission()
    {
        // A handler station manager holds no CreateFloorPlan: the authorization middleware answers 403 before any filter, so three
        // refusals spend none of the user's allowance of two, and the same user, once an administrator, has it whole.
        var services = new Services();
        await using var app = MainHost(services);
        using var client = app.CreateClient();

        var refused = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = Request("FloorPlans.Upload", "planner");
            request.Headers.Remove(TestAuthenticationHandler.RolesHeader);
            request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.HandlerStationManager);
            using var answer = await client.SendAsync(request, Ct);
            refused.Add(answer.StatusCode);
        }

        var allowed = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            using var answer = await client.SendAsync(Request("FloorPlans.Upload", "planner"), Ct);
            allowed.Add(answer.StatusCode);
        }

        using var third = await client.SendAsync(Request("FloorPlans.Upload", "planner"), Ct);

        refused.Should().Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden]);
        allowed.Should().NotContain(HttpStatusCode.TooManyRequests, "the allowance is whole");
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        services.Calls("FloorPlans.Upload").Should().Be(2);
    }

    [Fact]
    public async Task Import_Should_Answer409WithTheFixedText_When_TheSiteLockWasHeldLongerThanTheWait()
    {
        var services = new Services();
        services.Configuration.Setup(c => c.ImportAsync(Site, It.IsAny<ImportSiteConfigurationRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Error<SiteConfigurationImportViewModel>(SiteConfigurationErrors.SiteBusy));
        await using var app = MainHost(services);
        using var client = app.CreateClient();

        using var busy = await client.SendAsync(Request("SiteConfiguration.Import", "planner"), Ct);

        busy.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await busy.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("title").GetString().Should().Be("Conflict");
        problem.RootElement.GetProperty("detail").GetString().Should().Be(SiteConfigurationErrors.SiteBusy);
    }

    [Fact]
    public async Task Endpoints_Should_EachCarryTheLimitWithItsKind_When_TheUploadPolicyHoldsThem()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var uploads = endpoints.Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitingExtensions.UploadPolicy)
            .ToDictionary(Action, e => e.Metadata.GetMetadata<UploadLimitAttribute>()?.Kind, StringComparer.Ordinal);
        var limited = endpoints.Where(e => e.Metadata.GetMetadata<UploadLimitAttribute>() is not null).Select(Action).ToList();

        uploads.Should().BeEquivalentTo(new Dictionary<string, string>(Kinds, StringComparer.Ordinal)
        {
            ["DeviceImports.Preview"] = UploadLimitAttribute.CsvPreview,
            ["DeviceImports.Commit"] = UploadLimitAttribute.CsvCommit,
            ["DeskCodeMappingImports.Preview"] = UploadLimitAttribute.CsvPreview,
            ["DeskCodeMappingImports.Commit"] = UploadLimitAttribute.CsvCommit
        }, "every endpoint of the upload policy is counted per user, each kind apart");
        limited.Should().BeEquivalentTo(uploads.Keys, "the limit is the upload policy's");
        new UploadLimitAttribute(UploadLimitAttribute.FloorPlan).Order.Should().BeNegative("before the steady upload's and the advertisement upload's reads (order 0)");
    }

    /// <summary>
    /// ARV-265: one user's upload held in its service (a slow transfer) holds one place on the host. The same user's next upload,
    /// of that kind or another, is refused at once (429, Retry-After 10, the fixed text) before its service; two other users'
    /// uploads take the two waiting places; a fourth user's is refused by the host, which spends none of their allowance; a request
    /// that sends no credentials waits for no place (401). Once the first upload ends the waiting ones run, every place is free
    /// again, and the first user may upload again.
    /// </summary>
    [Fact]
    public async Task Upload_Should_LeaveOtherUsersThePlaces_When_OneUsersUploadIsUnderway()
    {
        var services = new Services();
        var (entered, release) = services.HoldFloorPlans();
        await using var app = MainHost(services);
        using var client = app.CreateClient();
        var limiter = app.Services.GetRequiredService<UploadRateLimiter>();

        var first = client.SendAsync(Request("FloorPlans.Upload", "hog"), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var again = await client.SendAsync(Request("FloorPlans.Upload", "hog"), Ct);
        using var otherKind = await client.SendAsync(Request("FlightSchedules.Preview", "hog"), Ct);
        var second = client.SendAsync(Request("FloorPlans.Upload", "second-user"), Ct);
        var third = client.SendAsync(Request("FloorPlans.Upload", "third-user"), Ct);
        await UntilAsync(() => limiter.Places().CurrentQueuedCount == 2, "two uploads waiting");
        using var busy = await client.SendAsync(Request("FloorPlans.Upload", "fourth-user"), Ct);
        using var unsigned = Request("FloorPlans.Upload", "nobody");
        unsigned.Headers.Remove(TestAuthenticationHandler.UserHeader);
        using var anonymous = await client.SendAsync(unsigned, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // A credential that fails authentication (the test scheme ignores the bearer without its user): before ARV-265 the rate
        // limiter counted such a request in the host's places, before authentication; now it is refused 401 at once.
        using var forgedRequest = Request("FloorPlans.Upload", "nobody");
        forgedRequest.Headers.Remove(TestAuthenticationHandler.UserHeader);
        forgedRequest.Headers.Authorization = AuthenticationHeaderValue.Parse(TestAuthenticationHandler.Bearer);
        using var forged = await client.SendAsync(forgedRequest, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await RefusedAsync(again, UploadLimitAttribute.UploadUnderway);
        await RefusedAsync(otherKind, UploadLimitAttribute.UploadUnderway);
        await RefusedAsync(busy, UploadLimitAttribute.UploadsBusy);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a request that sends no credentials takes no place and waits for none");
        forged.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a credential that fails authentication takes no place and waits for none");
        limiter.Places().CurrentQueuedCount.Should().Be(2, "neither took a waiting place");
        (second.IsCompleted, third.IsCompleted).Should().Be((false, false), "both wait for the permit");
        services.Calls("FloorPlans.Upload").Should().Be(1, "only the first reached its service");
        services.Calls("FlightSchedules.Preview").Should().Be(0);

        release.SetResult();
        var answers = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        try
        {
            answers.Should().OnlyContain(a => a.StatusCode != HttpStatusCode.TooManyRequests, "the waiting uploads run once the permit is free");
        }
        finally
        {
            foreach (var answer in answers)
                answer.Dispose();
        }

        services.Calls("FloorPlans.Upload").Should().Be(3);
        var fourth = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            using var allowed = await client.SendAsync(Request("FloorPlans.Upload", "fourth-user"), Ct);
            fourth.Add(allowed.StatusCode);
        }

        using var hogAgain = await client.SendAsync(Request("FloorPlans.Upload", "hog"), Ct);
        fourth.Should().NotContain(HttpStatusCode.TooManyRequests, "the host's refusal spent none of their allowance of two");
        hogAgain.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "their upload has ended");
        (limiter.Places().CurrentAvailablePermits, limiter.Places().CurrentQueuedCount).Should().Be((1, 0), "every place is free again");
    }

    /// <summary>
    /// ARV-265 (its security review): a request cancelled while it waits for the permit (its client gone, or its timeout) leaves
    /// the queue and gives back its user's share, so that user's next upload waits for the permit rather than being refused.
    /// </summary>
    [Fact]
    public async Task Upload_Should_FreeItsWaitingPlaceAndItsShare_When_ItIsCancelledWhileWaiting()
    {
        var services = new Services();
        var (entered, release) = services.HoldFloorPlans();
        await using var app = MainHost(services);
        using var client = app.CreateClient();
        var limiter = app.Services.GetRequiredService<UploadRateLimiter>();
        var first = client.SendAsync(Request("FloorPlans.Upload", "holder"), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using var gone = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = client.SendAsync(Request("FloorPlans.Upload", "leaver"), gone.Token);
        await UntilAsync(() => limiter.Places().CurrentQueuedCount == 1, "one upload waiting");
        await gone.CancelAsync();
        var cancelled = () => waiting;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await UntilAsync(() => limiter.Places().CurrentQueuedCount == 0, "the cancelled upload's waiting place freed");

        var again = client.SendAsync(Request("FloorPlans.Upload", "leaver"), Ct);
        await UntilAsync(() => limiter.Places().CurrentQueuedCount == 1, "the same user's next upload waiting");
        again.IsCompleted.Should().BeFalse("their share was given back: the upload waits for the permit, it is not refused");
        release.SetResult();
        using var firstAnswer = await first.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var againAnswer = await again.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        againAnswer.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        services.Calls("FloorPlans.Upload").Should().Be(2, "the holder's and the leaver's second upload");
        (limiter.Places().CurrentAvailablePermits, limiter.Places().CurrentQueuedCount).Should().Be((1, 0));
    }

    /// <summary>
    /// ARV-265 (its security review): only Ariva.Api.Main's endpoints carry the <c>upload</c> policy, whose places
    /// <c>UploadLimitAttribute</c> takes; the rate limiting middleware takes nothing for it on any host, so an endpoint of another
    /// host under it would be unlimited.
    /// </summary>
    [Theory]
    [InlineData(ArivaHosts.Ingest)]
    [InlineData(ArivaHosts.Integration)]
    public async Task Endpoints_Should_NotUseTheUploadPolicy_When_TheHostIsNotMain(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var uploads = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitingExtensions.UploadPolicy)
            .Select(e => e.DisplayName).ToList();

        uploads.Should().BeEmpty($"{host} has no UploadLimitAttribute to take the upload places");
    }

    /// <summary>
    /// ARV-265: the caller's window is checked before a place is taken, so a user past it is refused at once while the permit is
    /// held, without waiting for it or taking a waiting place.
    /// </summary>
    [Fact]
    public async Task Upload_Should_BeRefusedWithoutAPlace_When_TheUserIsPastTheirWindow()
    {
        var services = new Services();
        await using var app = MainHost(services);
        using var client = app.CreateClient();
        var limiter = app.Services.GetRequiredService<UploadRateLimiter>();
        for (var i = 0; i < 2; i++)
            (await client.SendAsync(Request("FloorPlans.Upload", "eager"), Ct)).Dispose();
        var (entered, release) = services.HoldFloorPlans();
        var holder = client.SendAsync(Request("FloorPlans.Upload", "holder"), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using var refused = await client.SendAsync(Request("FloorPlans.Upload", "eager"), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.Should().NotBeNull();
        refused.Headers.RetryAfter.Delta.Should().Be(TimeSpan.FromSeconds(610), "the window's, not a place's");
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)))
            problem.RootElement.GetProperty("detail").GetString().Should().Be(UploadLimitAttribute.TooManyUploads);
        limiter.Places().CurrentQueuedCount.Should().Be(0, "it took no waiting place");
        release.SetResult();
        (await holder).Dispose();
    }

    /// <summary>ARV-265: a share of three (the E2E suite's) lets one user hold every place, and the host still takes no more.</summary>
    [Fact]
    public async Task Upload_Should_LetOneUserHoldEveryPlace_When_TheShareIsTheHostsPlaces()
    {
        var services = new Services();
        var (entered, release) = services.HoldFloorPlans();
        await using var app = MainHost(services, share: 3);
        using var client = app.CreateClient();
        var limiter = app.Services.GetRequiredService<UploadRateLimiter>();

        var held = new List<Task<HttpResponseMessage>> { client.SendAsync(Request("FloorPlans.Upload", "suite"), Ct) };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        held.Add(client.SendAsync(Request("FloorPlans.Upload", "suite"), Ct));
        held.Add(client.SendAsync(Request("FloorPlans.Upload", "suite"), Ct));
        await UntilAsync(() => limiter.Places().CurrentQueuedCount == 2, "two uploads waiting");
        using var busy = await client.SendAsync(Request("FloorPlans.Upload", "suite"), Ct);

        await RefusedAsync(busy, UploadLimitAttribute.UploadUnderway);
        release.SetResult();
        foreach (var answer in await Task.WhenAll(held).WaitAsync(TimeSpan.FromSeconds(30), Ct))
            answer.Dispose();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    public void IsValid_Should_TakeOneToTheHostsPlaces_When_TheShareIsSet(int permitLimit, bool valid) =>
        new UploadShareSettings { PermitLimit = permitLimit }.IsValid.Should().Be(valid);

    [Fact]
    public void SettingsOf_Should_GiveTheCsvImportsTheirOwnWindow_When_Asked()
    {
        var settings = new RateLimitingSettings
        {
            CsvImports = new SlidingWindowSettings { PermitLimit = 6, WindowSeconds = 600, SegmentsPerWindow = 60 },
            Uploads = new SlidingWindowSettings { PermitLimit = 10, WindowSeconds = 300, SegmentsPerWindow = 30 }
        };
        using var limiter = new UploadRateLimiter(Options.Create(settings));

        limiter.SettingsOf(UploadLimitAttribute.CsvPreview).Should().BeSameAs(settings.CsvImports);
        limiter.SettingsOf(UploadLimitAttribute.CsvCommit).Should().BeSameAs(settings.CsvImports);
        foreach (var kind in Kinds.Values)
            limiter.SettingsOf(kind).Should().BeSameAs(settings.Uploads, kind);
    }

    [Fact]
    public void TryAcquire_Should_RefuseWithTheWindowAndOneSegmentAsRetryAfter_When_TheLimitIsTaken()
    {
        using var limiter = new UploadRateLimiter(Options.Create(new RateLimitingSettings
        {
            Uploads = new SlidingWindowSettings { PermitLimit = 2, WindowSeconds = 300, SegmentsPerWindow = 30 }
        }));

        var first = limiter.TryAcquire("id:a", UploadLimitAttribute.FloorPlan);
        var second = limiter.TryAcquire("id:a", UploadLimitAttribute.FloorPlan);
        var third = limiter.TryAcquire("id:a", UploadLimitAttribute.FloorPlan);
        var otherKind = limiter.TryAcquire("id:a", UploadLimitAttribute.AdvertisingMedia);
        var otherCaller = limiter.TryAcquire("id:b", UploadLimitAttribute.FloorPlan);

        (first.Allowed, second.Allowed, third.Allowed).Should().Be((true, true, false));
        third.RetryAfter.Should().Be(TimeSpan.FromSeconds(310), "the window of 300 s and one segment of 10 s");
        otherKind.Allowed.Should().BeTrue("each kind has its own allowance");
        otherCaller.Allowed.Should().BeTrue("each caller has their own allowance");
    }

    [Fact]
    public void CallerOf_Should_PreferTheUserIdAndRefuseACallerWithNeither_When_Asked()
    {
        var id = Guid.Parse("0199a000-0000-7000-8000-000000000155");

        UploadLimitAttribute.CallerOf(User(id, "planner")).Should().Be("id:" + id.ToString("N", CultureInfo.InvariantCulture));
        UploadLimitAttribute.CallerOf(User(null, "planner")).Should().Be("name:planner", "only the test scheme's principals have no id");
        UploadLimitAttribute.CallerOf(User(null, null)).Should().BeNull();
        UploadLimitAttribute.CallerOf(User(null, "")).Should().BeNull();
        UploadLimitAttribute.CallerOf(null).Should().BeNull();
    }

    [Theory]
    [InlineData(0, 600, 60)]
    [InlineData(10, 0, 1)]
    [InlineData(10, 600, 0)]
    [InlineData(10, 30, 31)]
    [InlineData(10, 86_401, 60)]
    [InlineData(10, 3_600, 601)]
    public void IsValid_Should_BeFalse_When_AWindowIsOutOfBounds(int permitLimit, int windowSeconds, int segmentsPerWindow)
    {
        new SlidingWindowSettings { PermitLimit = permitLimit, WindowSeconds = windowSeconds, SegmentsPerWindow = segmentsPerWindow }.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(10, 86_400, 600)]
    [InlineData(10_000, 600, 600)]
    public void IsValid_Should_BeTrue_When_AWindowIsAtItsBounds(int permitLimit, int windowSeconds, int segmentsPerWindow)
    {
        new SlidingWindowSettings { PermitLimit = permitLimit, WindowSeconds = windowSeconds, SegmentsPerWindow = segmentsPerWindow }.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Defaults_Should_CountTheCsvImportsAndTheUploadsInSlidingWindows_When_NotConfigured()
    {
        var settings = new RateLimitingSettings();

        (settings.CsvImports.PermitLimit, settings.CsvImports.WindowSeconds, settings.CsvImports.SegmentsPerWindow).Should().Be((6, 600, 60));
        (settings.Uploads.PermitLimit, settings.Uploads.WindowSeconds, settings.Uploads.SegmentsPerWindow).Should().Be((10, 600, 60));
        settings.CsvImports.IsValid.Should().BeTrue();
        settings.Uploads.IsValid.Should().BeTrue();
        settings.UploadShare.PermitLimit.Should().Be(1, "one place a user (ARV-265)");
        settings.UploadShare.IsValid.Should().BeTrue();
    }

    #endregion

    #region Helpers

    private static IArivaHost MainHost(Services services, int csvImports = 2, int share = 1) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            builder.UseSetting("Security:RateLimiting:UploadShare:PermitLimit", share.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Security:RateLimiting:Uploads:PermitLimit", "2");
            builder.UseSetting("Security:RateLimiting:Uploads:WindowSeconds", "600");
            builder.UseSetting("Security:RateLimiting:CsvImports:PermitLimit", csvImports.ToString(CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(collection =>
            {
                TestAuthenticationHandler.Register(collection);
                FakeAdministration.Register(collection);
                collection.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
                services.Register(collection);
            });
        });

    /// <summary>The endpoint's request as an administrator with a recent second factor would send it (each is a critical action or needs none).</summary>
    private static HttpRequestMessage Request(string endpoint, string user)
    {
        var (method, path, fields) = Shape(endpoint);
        var request = Signed(method, path, user);
        if (method == HttpMethod.Get)
            return request;
        if (fields is null)
        {
            var bundle = new
            {
                formatVersion = 1, sourceSite = Site, exportedAtUtc = "2026-10-20T08:00:00Z",
                manifest = new Dictionary<string, string>(), parts = new Dictionary<string, string>()
            };
            request.Content = new StringContent(JsonSerializer.Serialize(bundle), Encoding.UTF8, "application/json");
            return request;
        }

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("content\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "upload.bin");
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);
        request.Content = form;
        return request;
    }

    /// <summary>A body cut short: the endpoint refuses it 400 once it reads it (a form by the upload filter, a bundle by model binding).</summary>
    private static HttpRequestMessage Malformed(string endpoint, string user)
    {
        var (method, path, fields) = Shape(endpoint);
        var request = Signed(method, path, user);
        if (fields is null)
        {
            request.Content = new StringContent("{\"formatVersion\": 1, \"parts\": {", Encoding.UTF8, "application/json");
            return request;
        }

        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("--cut\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.bin\"\r\n\r\ncode"));
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=cut");
        return request;
    }

    private static (HttpMethod Method, string Path, Dictionary<string, string> Fields) Shape(string endpoint) => endpoint switch
    {
        "FloorPlans.Upload" => (HttpMethod.Post, $"/api/v1/admin/levels/{LevelId}/floor-plan",
            new Dictionary<string, string> { ["metresPerPixel"] = "0.05", ["originX"] = "0", ["originY"] = "0" }),
        "FlightSchedules.Preview" => (HttpMethod.Post, $"/api/v1/admin/sites/{Site}/flight-schedules/preview", new Dictionary<string, string> { ["horizonDays"] = "7" }),
        "FlightSchedules.Import" => (HttpMethod.Post, $"/api/v1/admin/sites/{Site}/flight-schedules/import",
            new Dictionary<string, string> { ["horizonDays"] = "7", ["previewToken"] = "token" }),
        "DeviceImports.Preview" => (HttpMethod.Post, $"/api/v1/admin/sites/{Site}/device-imports/preview", new Dictionary<string, string>()),
        "SiteConfiguration.Export" => (HttpMethod.Get, $"/api/v1/admin/sites/{Site}/configuration", null),
        "SiteConfiguration.Import" => (HttpMethod.Post, $"/api/v1/admin/sites/{Site}/configuration", null),
        "AdvertisingMedia.Upload" => (HttpMethod.Post, $"/api/v1/admin/sites/{Site}/advertising/media", new Dictionary<string, string>
        {
            ["advertiser"] = "Gulf Air", ["campaign"] = "Winter sun", ["startsOn"] = "2026-10-20", ["endsOn"] = "2026-10-31"
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not an upload endpoint of these tests.")
    };

    private static HttpRequestMessage Signed(HttpMethod method, string path, string user)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, user);
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);
        request.Headers.Add(TestAuthenticationHandler.MethodsHeader, "pwd,otp");
        request.Headers.Add(TestAuthenticationHandler.AuthTimeHeader, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return request;
    }

    /// <summary>A place refused (ARV-265): 429 with the concurrency policies' Retry-After and the fixed text.</summary>
    private static async Task RefusedAsync(HttpResponseMessage answer, string text)
    {
        answer.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        answer.Headers.RetryAfter.Should().NotBeNull();
        answer.Headers.RetryAfter.Delta.Should().Be(TimeSpan.FromSeconds(RateLimitingExtensions.ConcurrencyRetryAfterSeconds));
        using var problem = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("title").GetString().Should().Be("Too many requests");
        problem.RootElement.GetProperty("detail").GetString().Should().Be(text);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException(what + ": not within 15 s");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>A controller action as <c>Controller.Action</c> (the controller's name without its suffix), or the endpoint's name.</summary>
    private static string Action(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action ? $"{action.ControllerName}.{action.ActionName}" : endpoint.DisplayName ?? string.Empty;

    private static ICurrentUser User(Guid? id, string name)
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(u => u.Id).Returns(id);
        user.SetupGet(u => u.UserName).Returns(name);
        return user.Object;
    }

    /// <summary>Every upload service, refusing what reaches it as not found (the limit's tests count calls, not answers).</summary>
    private sealed class Services
    {
        public Mock<ISvcFloorPlans> Plans { get; } = new(MockBehavior.Strict);
        public Mock<ISvcFlightSchedules> Schedules { get; } = new(MockBehavior.Strict);
        public Mock<ISvcSiteConfiguration> Configuration { get; } = new(MockBehavior.Strict);
        public Mock<ISvcAdvertisingPolicies> Policies { get; } = new(MockBehavior.Strict);
        public Mock<ISvcAdvertisingMedia> Media { get; } = new(MockBehavior.Strict);
        public Mock<ISvcDeviceImports> Devices { get; } = new(MockBehavior.Strict);

        public Services()
        {
            Plans.Setup(p => p.UploadAsync(LevelId, It.IsAny<FloorPlanUpload>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<FloorPlanViewModel>(TopologyErrors.NotFound));
            Schedules.Setup(s => s.SiteExistsAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Schedules.Setup(s => s.PreviewAsync(Site, It.IsAny<Stream>(), 7, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<SsimPreviewViewModel>(TopologyErrors.NotFound));
            Schedules.Setup(s => s.ImportAsync(Site, It.IsAny<Stream>(), 7, "token", It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<SsimImportViewModel>(TopologyErrors.NotFound));
            Configuration.Setup(c => c.ExportAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<SiteConfigurationExportViewModel>(TopologyErrors.NotFound));
            Configuration.Setup(c => c.ImportAsync(Site, It.IsAny<ImportSiteConfigurationRequest>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Error<SiteConfigurationImportViewModel>(TopologyErrors.NotFound));
            Policies.Setup(p => p.AccessAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(AdvertisingAccess.Allowed);
            Media.Setup(m => m.UploadAsync(Site, It.IsAny<AdvertisingMediaUpload>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound));
            Devices.Setup(d => d.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Devices.Setup(d => d.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Error<DeviceImportPreviewViewModel>(CsvImportErrors.NoRows));
        }

        public void Register(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Scoped(_ => Plans.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Schedules.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Configuration.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Policies.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Media.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => Devices.Object));
        }

        /// <summary>Holds every floor plan upload in its service until released (ARV-265: a slow transfer holding its place).</summary>
        public (TaskCompletionSource Entered, TaskCompletionSource Release) HoldFloorPlans()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Plans.Setup(p => p.UploadAsync(LevelId, It.IsAny<FloorPlanUpload>(), It.IsAny<CancellationToken>())).Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return Result.Error<FloorPlanViewModel>(TopologyErrors.NotFound);
            });
            return (entered, release);
        }

        /// <summary>How many requests of the endpoint reached its service.</summary>
        public int Calls(string endpoint) => endpoint switch
        {
            "FloorPlans.Upload" => Count(Plans, nameof(ISvcFloorPlans.UploadAsync)),
            "FlightSchedules.Preview" => Count(Schedules, nameof(ISvcFlightSchedules.PreviewAsync)),
            "FlightSchedules.Import" => Count(Schedules, nameof(ISvcFlightSchedules.ImportAsync)),
            "SiteConfiguration.Export" => Count(Configuration, nameof(ISvcSiteConfiguration.ExportAsync)),
            "SiteConfiguration.Import" => Count(Configuration, nameof(ISvcSiteConfiguration.ImportAsync)),
            "AdvertisingMedia.Upload" => Count(Media, nameof(ISvcAdvertisingMedia.UploadAsync)),
            "DeviceImports.Preview" => Count(Devices, nameof(ISvcDeviceImports.PreviewAsync)),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not an upload endpoint of these tests.")
        };

        private static int Count<T>(Mock<T> mock, string method) where T : class => mock.Invocations.Count(i => i.Method.Name == method);
    }

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }

    #endregion
}
