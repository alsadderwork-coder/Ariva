using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Settings;
using Ariva.Api.Main.Controllers.AdminArea.Advertising;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Topology;
using Ariva.Core.Services.Validation;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-096, second security review (CWE-400, CWE-770, ASVS 5.0 V15.2.2): every endpoint of the <c>upload</c> concurrency policy reads
/// its body steadily (<see cref="SteadyUploadAttribute"/>, or the advertisement upload's filter, which takes the same steps). Over
/// HTTP/1.x the body must arrive at 32 KB a second after 10 seconds, and the filter reads it with the request's token, so the
/// <c>transfer</c> request timeout ends a read that MVC's own readers (which take no token) would let run. On a real Kestrel (TestServer
/// has no minimum data rate), a CSV import trickled at 4 KB a second (above Kestrel's own 240 bytes a second, so only the filter's rate
/// can cut it) holds the host's one upload slot, so beside two other users' uploads waiting a third is refused 429, until it is cut
/// off with 408 a few seconds after the grace instead of the four minutes its declared length would take; the waiting uploads then
/// run and the next is not refused. The JSON site configuration import is cut the same way, with the same fixed 408. ARV-265: one
/// user's trickled uploads hold one place, so their next ones are refused before their bodies are asked for and two other users'
/// uploads take the waiting places, while a request that sends no credentials takes no place and waits for none.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SteadyUploadTests
{
    private const string Boundary = "arivasteady";
    private const string Site = "DMO";
    private const string CsvPreview = $"/api/v1/admin/sites/{Site}/desk-code-mapping-imports/preview";
    private const string ConfigurationImport = $"/api/v1/admin/sites/{Site}/configuration";

    /// <summary>A trickle above Kestrel's own minimum (240 bytes a second) and far below the filter's: 1 KB every 250 ms.</summary>
    private const int TrickleBytes = 1024;

    private static readonly TimeSpan TrickleEvery = TimeSpan.FromMilliseconds(250);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Inventory

    [Fact]
    public async Task Endpoints_Should_ReadEveryUploadSteadily_When_MainMapsThem()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var uploads = RouteEndpoints(app).Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitingExtensions.UploadPolicy).ToList();

        uploads.Select(Action).Should().Contain(new[]
        {
            "FloorPlans.Upload", "FlightSchedules.Preview", "FlightSchedules.Import", "DeviceImports.Preview", "DeviceImports.Commit",
            "DeskCodeMappingImports.Preview", "DeskCodeMappingImports.Commit", "SiteConfiguration.Import", "AdvertisingMedia.Upload"
        });
        uploads.Should().OnlyContain(e => e.Metadata.GetMetadata<SteadyUploadAttribute>() != null || e.Metadata.GetMetadata<AdvertisingUploadLimitAttribute>() != null,
            "a body trickling into the one upload slot a host has must be cut off, and its read must take the request's token (ARV-096)");
        uploads.Where(e => e.Metadata.GetMetadata<AdvertisingUploadLimitAttribute>() != null).Select(Action).Should().Equal(["AdvertisingMedia.Upload"],
            "only the advertisement upload sets its own size per installation, through the same steps");
    }

    [Fact]
    public async Task Endpoints_Should_NameTheirOwnRefusalOfAFileTooLarge_When_TheyCarryTheSteadyUpload()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var steady = RouteEndpoints(app).Where(e => e.Metadata.GetMetadata<SteadyUploadAttribute>() != null).ToList();

        steady.Should().NotBeEmpty();
        steady.Should().OnlyContain(e => e.Metadata.GetMetadata<SteadyUploadAttribute>().TooLarge != SteadyUploadAttribute.TooLargeByDefault,
            "a body past the limit answers 413 with the same text as the endpoint's own refusal of a file too large");
        steady.Should().OnlyContain(e => RateLimitingExtensions.ConcurrencyPolicies.Contains(e.Metadata.GetMetadata<EnableRateLimitingAttribute>().PolicyName),
            "the steady read is for a body that holds a permit of its host");
    }

    [Fact]
    public async Task Endpoints_Should_ReadEveryBodySteadily_When_AConcurrencyPolicyHoldsThem()
    {
        // ARV-096b: every concurrency policy, not only the uploads': a body arriving slowly holds its permit, and MVC's JSON formatter
        // reads without the request's token, so a request timeout alone would not end it.
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var held = RouteEndpoints(app).Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName is { } policy &&
                                                  RateLimitingExtensions.ConcurrencyPolicies.Contains(policy)).ToList();
        var bodies = held.Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>().Parameters.Any(p => p.BindingInfo?.BindingSource is { } source &&
                                         (source == BindingSource.Body || source == BindingSource.Form || source == BindingSource.FormFile))).ToList();

        bodies.Select(Action).Should().Contain(["AlertRules.Backtest", "ValidationResults.Recompute"]);
        bodies.Should().OnlyContain(e => e.Metadata.GetMetadata<SteadyUploadAttribute>() != null || e.Metadata.GetMetadata<AdvertisingUploadLimitAttribute>() != null);
        held.Except(bodies).Where(e => e.Metadata.GetMetadata<SteadyUploadAttribute>() == null && e.Metadata.GetMetadata<AdvertisingUploadLimitAttribute>() == null)
            .Should().OnlyContain(e => e.Metadata.GetMetadata<HttpMethodMetadata>().HttpMethods.SequenceEqual(new[] { "GET" }), "the others bind no body");
    }

    [Fact]
    public async Task Endpoints_Should_HaveTheirBodiesReadSteadily_When_AConcurrencyPolicyHoldsThemOnIntegration()
    {
        // The Integration API's batches and AIDX messages bind no body through MVC: the call audit reads it first, under the rate
        // UseSteadyIntegrationBodies sets for their policies (HostRequestPathTests).
        await using var app = ArivaHosts.Create(ArivaHosts.Integration);

        var held = RouteEndpoints(app).Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName is { } policy &&
                                                  RateLimitingExtensions.ConcurrencyPolicies.Contains(policy)).ToList();

        held.Should().NotBeEmpty();
        held.Should().OnlyContain(e => SteadyIntegrationBodies.Policies.Contains(e.Metadata.GetMetadata<EnableRateLimitingAttribute>().PolicyName));
        held.Should().OnlyContain(e => e.Metadata.GetMetadata<Ariva.Api.Common.Security.IntegrationScopeAttribute>() != null,
            "the call audit reads the bodies of the scoped endpoints, after the rate is set");
    }

    [Fact]
    public void Rate_Should_BringTheLargestBodiesInWithinTheTransferTimeout_When_TheyArriveAtTheSlowestAllowed()
    {
        // 20 MB and the form's framing (a floor plan, an SSIM file; the site configuration bundle is 20 MB alone) and a 1 MB CSV.
        const double largest = (20 * 1024 * 1024) + (64 * 1024);
        const double csv = 1024 * 1024;

        var largestSeconds = largest / SteadyUploadAttribute.MinBytesPerSecond;
        var csvSeconds = csv / SteadyUploadAttribute.MinBytesPerSecond;

        largestSeconds.Should().BeApproximately(642, 1, "about 11 minutes");
        largestSeconds.Should().BeLessThan(new RequestTimeoutsSettings().TransferSeconds, "within the 15 minute transfer timeout");
        csvSeconds.Should().Be(32);
        SteadyUploadAttribute.MinimumRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(SteadyUploadAttribute.GraceSeconds)).And.Be(TimeSpan.FromSeconds(10));
        AdvertisingUploadLimitAttribute.MinBytesPerSecond.Should().Be(SteadyUploadAttribute.MinBytesPerSecond, "one rate for every upload");
    }

    #endregion

    #region The filter

    [Fact]
    public async Task Filter_Should_SetTheMinimumRate_When_TheUploadIsOverHttp1()
    {
        var context = Context(Form("file", "a,b"));
        var rate = new BodyRate(refuses: false);
        context.HttpContext.Features.Set<IHttpMinRequestBodyDataRateFeature>(rate);
        context.HttpContext.Request.Protocol = "HTTP/1.1";

        (await RunAsync(context)).Should().BeTrue();

        rate.MinDataRate.BytesPerSecond.Should().Be(32 * 1024);
        rate.MinDataRate.GracePeriod.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("HTTP/2")]
    [InlineData("HTTP/3")]
    public async Task Filter_Should_LeaveTheRateAlone_When_TheUploadIsOverHttp2Or3(string protocol)
    {
        var context = Context(Form("file", "a,b"));
        context.HttpContext.Features.Set<IHttpMinRequestBodyDataRateFeature>(new BodyRate(refuses: true));
        context.HttpContext.Request.Protocol = protocol;

        (await RunAsync(context)).Should().BeTrue("the connection's own rate applies; nothing throws");
    }

    [Fact]
    public async Task Filter_Should_ReadTheFormBeforeModelBinding_When_TheBodyIsAForm()
    {
        var context = Context(Form("previewToken", "abc"));

        (await RunAsync(context)).Should().BeTrue();

        context.HttpContext.Features.Get<IFormFeature>()?.Form.Should().NotBeNull("model binding takes the form already read");
        context.HttpContext.Request.Form["previewToken"].ToString().Should().Be("abc");
    }

    [Fact]
    public async Task Filter_Should_ReadWithTheRequestsToken_When_TheTransferTimeoutCancelsIt()
    {
        // The request timeout cancels RequestAborted; MVC's own form reader would not see it. The cancellation is the request
        // timeout's to answer (503), so the filter neither answers it nor calls it a malformed form.
        using var timeout = new CancellationTokenSource();
        await timeout.CancelAsync();
        var form = Context(Form("previewToken", "abc"));
        form.HttpContext.RequestAborted = timeout.Token;
        var json = Context(Encoding.UTF8.GetBytes("{\"formatVersion\":1}"), "application/json", bindsBody: true);
        json.HttpContext.RequestAborted = timeout.Token;

        var readForm = () => RunAsync(form);
        var readJson = () => RunAsync(json);

        await readForm.Should().ThrowAsync<OperationCanceledException>();
        await readJson.Should().ThrowAsync<OperationCanceledException>();
        form.Result.Should().BeNull();
        json.Result.Should().BeNull();
    }

    [Fact]
    public async Task Filter_Should_Answer408WithFixedText_When_TheBodyArrivesTooSlowly()
    {
        var form = Context([], contentLength: null);
        form.HttpContext.Request.Body = new KestrelRefusedBody(StatusCodes.Status408RequestTimeout);
        var json = Context([], "application/json", contentLength: null, bindsBody: true);
        json.HttpContext.Request.Body = new KestrelRefusedBody(StatusCodes.Status408RequestTimeout);

        (await RunAsync(form)).Should().BeFalse();
        (await RunAsync(json)).Should().BeFalse();

        foreach (var context in new[] { form, json })
            Problem(context, StatusCodes.Status408RequestTimeout).Detail.Should().Be(SteadyUploadAttribute.Unreadable);
    }

    [Fact]
    public async Task Filter_Should_Answer413WithTheEndpointsText_When_TheBodyGrowsPastItsLimit()
    {
        // Sent without a length, as a chunked sender does: Kestrel stops the read at the limit.
        var form = Context([], contentLength: null);
        form.HttpContext.Request.Body = new KestrelRefusedBody(StatusCodes.Status413PayloadTooLarge);
        var json = Context([], "application/json", contentLength: null, bindsBody: true);
        json.HttpContext.Request.Body = new KestrelRefusedBody(StatusCodes.Status413PayloadTooLarge);
        var declared = Context(Encoding.UTF8.GetBytes("{}"), "application/json", contentLength: 2 * 1024 * 1024, bindsBody: true);
        var filter = new SteadyUploadAttribute { TooLarge = CsvImportErrors.FileTooLarge };

        foreach (var context in new[] { form, json, declared })
        {
            (await RunAsync(context, filter)).Should().BeFalse();
            var problem = Problem(context, StatusCodes.Status413PayloadTooLarge);
            problem.Title.Should().Be("Too large");
            problem.Detail.Should().Be(CsvImportErrors.FileTooLarge, "the endpoint's own text");
        }
    }

    [Fact]
    public async Task Filter_Should_Answer400WithFixedText_When_TheFormIsCutShort()
    {
        var context = Context(Form("file", "<script>alert(1)</script>")[..60]);

        (await RunAsync(context)).Should().BeFalse("the action never sees a form it cannot read");

        Problem(context, StatusCodes.Status400BadRequest).Detail.Should().Be(SteadyUploadAttribute.Unreadable, "never what was sent");
    }

    [Fact]
    public async Task Filter_Should_KeepTheWholeBodyRewound_When_TheActionBindsItFromTheBody()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"parts\":{\"topology\":\"" + new string('A', 100 * 1024) + "\"}}");
        var network = new OnceOnlyBody(bytes);
        var context = Context([], "application/json", contentLength: bytes.Length, bindsBody: true);
        context.HttpContext.Request.Body = network;

        (await RunAsync(context)).Should().BeTrue();

        network.ReadToTheEnd.Should().BeTrue("the filter read the body whole, with the request's token");
        context.HttpContext.Request.Body.Position.Should().Be(0);
        using var reader = new StreamReader(context.HttpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
        (await reader.ReadToEndAsync(Ct)).Should().Be(Encoding.UTF8.GetString(bytes), "model binding reads it again from the buffer");
    }

    [Fact]
    public async Task Filter_Should_LeaveTheBodyUnread_When_TheActionBindsNoBodyAndNoForm()
    {
        // A GET such as the site configuration export, or a body that is not a form sent to a form upload.
        var network = new OnceOnlyBody(Encoding.UTF8.GetBytes("{}"));
        var context = Context([], "application/json", contentLength: 2);
        context.HttpContext.Request.Body = network;

        (await RunAsync(context)).Should().BeTrue();

        network.Reads.Should().Be(0, "a body the action does not bind is never read");
    }

    [Fact]
    public async Task Filter_Should_ReadTheFormWithTheRequestsToken_When_TheActionBindsItsBodyAsJson()
    {
        // ARV-096, fifth review: a form sent to the JSON site configuration import is read here, with the request's token, rather
        // than left to MVC's form value providers, which would read it anyway without the token (so the transfer timeout would
        // not end it while it holds the one upload slot).
        var read = Context(Form("bundle", "x"), bindsBody: true);
        using var timeout = new CancellationTokenSource();
        await timeout.CancelAsync();
        var cancelled = Context(Form("bundle", "x"), bindsBody: true);
        cancelled.HttpContext.RequestAborted = timeout.Token;

        (await RunAsync(read)).Should().BeTrue();
        var readCancelled = () => RunAsync(cancelled);

        read.HttpContext.Features.Get<IFormFeature>()?.Form.Should().NotBeNull("the filter read the form itself");
        await readCancelled.Should().ThrowAsync<OperationCanceledException>();
        cancelled.Result.Should().BeNull("the cancellation is the request timeout's to answer");
    }

    #endregion

    #region A real Kestrel

    [Fact]
    public async Task Upload_Should_BeCutOffAndFreeTheSlot_When_ACsvImportTricklesBelowTheRate()
    {
        var imports = new Mock<ISvcDeskCodeMappingImports>();
        imports.Setup(i => i.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        imports.Setup(i => i.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Fluentx.Result<DeskCodeMappingImportPreviewViewModel>(new DeskCodeMappingImportPreviewViewModel("00", 1, [], [], 0, "token", null)));
        await using var app = KestrelMain(services => services.Replace(ServiceDescriptor.Scoped(_ => imports.Object)));
        var address = ArivaHosts.Address(app);

        // 1,000,000 bytes declared, within the 1 MB and 64 KB the endpoint takes: four minutes at the trickle's rate.
        await using var slow = await RawExchange.OpenAsync(address);
        await slow.SendAsync(Headers(CsvPreview, "multipart/form-data; boundary=" + Boundary, 1_000_000, administrator: true, expectContinue: true));
        await slow.ReadUntilAsync("HTTP/1.1 100 Continue\r\n\r\n", TimeSpan.FromSeconds(30));
        var started = Stopwatch.StartNew();
        await slow.SendAsync(FilePartHead());
        slow.Trickle(new string('a', TrickleBytes), TrickleEvery);

        // The 100 Continue came from the filter's first read, so the slow upload holds the one slot: two more wait, the next is refused
        // (three other users: one user holds one place at most, ARV-265).
        var probes = Enumerable.Range(0, 3).Select(i => FullCsvAsync(address, "prober-" + i.ToString(CultureInfo.InvariantCulture))).ToList();
        var refused = await await Task.WhenAny(probes);
        refused.Should().StartWith("HTTP/1.1 429", "one upload runs and two wait; the rest are refused");
        refused.Should().Contain("Retry-After: 10");
        probes.Remove(probes.First(p => p.IsCompleted));

        var answer = await slow.ReadAnswerAsync(TimeSpan.FromSeconds(60));
        var elapsed = started.Elapsed;

        TestContext.Current.SendDiagnosticMessage($"Trickled CSV import answered after {elapsed.TotalSeconds:0.0} s: {FirstLine(answer)}");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(40), "the trickle is cut a few seconds after the 10 second grace, not after its four minutes");
        elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(5), "the grace lets a slow start pick up");
        if (answer.Length > 0)
        {
            answer.Should().StartWith("HTTP/1.1 408");
            answer.Should().Contain("application/problem+json").And.Contain(SteadyUploadAttribute.Unreadable);
        }

        var waited = await Task.WhenAll(probes).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        waited.Should().OnlyContain(text => text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "the slot the trickle held is free again, and the waiting uploads run");
        (await FullCsvAsync(address)).Should().StartWith("HTTP/1.1 200", "the next upload is not refused");
        imports.Verify(i => i.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()), Times.Exactly(3), "only the whole uploads reached the service");
    }

    /// <summary>
    /// ARV-265, on a real Kestrel as the trickle above: one user trickles CSV previews at a rate the steady read cuts (so the test
    /// ends; at the 32 KB a second floor a transfer would hold its place as long as it lasts, about 32 s for this 1 MB file and 11
    /// minutes for 20 MB). Their first holds the permit, and their second and third are refused 429 at once with the fixed text,
    /// before their bodies are asked for. Two other users' uploads take the waiting places meanwhile, a request that sends no
    /// credentials is answered 401 at once with every place taken, and a fourth user's upload is refused by the host. Once the
    /// trickle is cut off, the waiting uploads run. Before ARV-265 the first user's three uploads took the permit and both waiting
    /// places before authentication, and every other user's upload was refused.
    /// </summary>
    [Fact]
    public async Task Upload_Should_LeaveTheWaitingPlacesToOtherUsers_When_OneUserTricklesThreeUploads()
    {
        var imports = new Mock<ISvcDeskCodeMappingImports>();
        imports.Setup(i => i.SiteReachableAsync(Site, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        imports.Setup(i => i.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Fluentx.Result<DeskCodeMappingImportPreviewViewModel>(new DeskCodeMappingImportPreviewViewModel("00", 1, [], [], 0, "token", null)));
        await using var app = KestrelMain(services => services.Replace(ServiceDescriptor.Scoped(_ => imports.Object)));
        var address = ArivaHosts.Address(app);
        var limiter = app.Services.GetRequiredService<Ariva.Api.Main.Controllers.AdminArea.Uploads.UploadRateLimiter>();

        await using var slow = await RawExchange.OpenAsync(address);
        await slow.SendAsync(Headers(CsvPreview, "multipart/form-data; boundary=" + Boundary, 1_000_000, administrator: true, expectContinue: true, user: "trickler"));
        await slow.ReadUntilAsync("HTTP/1.1 100 Continue\r\n\r\n", TimeSpan.FromSeconds(30));
        await slow.SendAsync(FilePartHead());
        slow.Trickle(new string('a', TrickleBytes), TrickleEvery);

        // Everything below happens within the trickle's grace: the other users first, then the refusals, each read only until its
        // answer is in (Kestrel keeps a connection open a while to drain a body it never asked for).
        var second = FullCsvAsync(address, "second-user");
        var third = FullCsvAsync(address, "third-user");
        await UntilAsync(() => limiter.Places().CurrentQueuedCount == 2, "two other users' uploads waiting");
        var refusals = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            await using var more = await RawExchange.OpenAsync(address);
            await more.SendAsync(Headers(CsvPreview, "multipart/form-data; boundary=" + Boundary, 1_000_000, administrator: true, expectContinue: true, user: "trickler"));
            refusals.Add(await more.ReadThroughAsync(Ariva.Api.Main.Controllers.AdminArea.Uploads.UploadLimitAttribute.UploadUnderway, TimeSpan.FromSeconds(10)));
        }

        string anonymous;
        await using (var unsigned = await RawExchange.OpenAsync(address))
        {
            await unsigned.SendAsync(Headers(CsvPreview, "multipart/form-data; boundary=" + Boundary, 1_000_000, administrator: true, expectContinue: true, signedIn: false));
            anonymous = await unsigned.ReadThroughAsync("\r\n\r\n", TimeSpan.FromSeconds(10));
        }

        // A credential that fails authentication: before ARV-265 the rate limiter counted it in the host's places.
        string forged;
        await using (var bearer = await RawExchange.OpenAsync(address))
        {
            await bearer.SendAsync(Headers(CsvPreview, "multipart/form-data; boundary=" + Boundary, 1_000_000, administrator: true, expectContinue: true, signedIn: false, forged: true));
            forged = await bearer.ReadThroughAsync("\r\n\r\n", TimeSpan.FromSeconds(10));
        }

        var queued = limiter.Places().CurrentQueuedCount;

        var fourth = await FullCsvAsync(address, "fourth-user");

        refusals.Should().HaveCount(2).And.OnlyContain(text => text.StartsWith("HTTP/1.1 429", StringComparison.Ordinal) && text.Contains("Retry-After: 10", StringComparison.Ordinal) &&
                                                           text.Contains(Ariva.Api.Main.Controllers.AdminArea.Uploads.UploadLimitAttribute.UploadUnderway, StringComparison.Ordinal) &&
                                                           !text.Contains("100 Continue", StringComparison.Ordinal),
            "one user holds one place: their next uploads are refused before their bodies are asked for");
        anonymous.Should().StartWith("HTTP/1.1 401", "a request that sends no credentials takes no place and waits for none");
        forged.Should().StartWith("HTTP/1.1 401", "a credential that fails authentication takes no place and waits for none");
        queued.Should().Be(2, "neither took a waiting place");
        fourth.Should().StartWith("HTTP/1.1 429").And.Contain(Ariva.Api.Main.Controllers.AdminArea.Uploads.UploadLimitAttribute.UploadsBusy);
        (second.IsCompleted, third.IsCompleted).Should().Be((false, false), "the other users' uploads wait for the permit");

        var answer = await slow.ReadAnswerAsync(TimeSpan.FromSeconds(60));
        TestContext.Current.SendDiagnosticMessage($"Trickled CSV import answered: {FirstLine(answer)}");
        var waited = await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        waited.Should().OnlyContain(text => text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "the waiting uploads run once the trickle is cut off");
        imports.Verify(i => i.PreviewAsync(Site, It.IsAny<Func<Stream>>(), It.IsAny<CancellationToken>()), Times.Exactly(2), "only the other users' whole uploads reached the service");
    }

    [Fact]
    public async Task Import_Should_Answer408ProblemDetails_When_ASiteConfigurationTricklesBelowTheRate()
    {
        var configuration = new Mock<ISvcSiteConfiguration>(MockBehavior.Strict);
        await using var app = KestrelMain(services => services.Replace(ServiceDescriptor.Scoped(_ => configuration.Object)));
        var address = ArivaHosts.Address(app);

        // 5,000,000 bytes declared, within the bundle's 20 MB: twenty minutes at the trickle's rate.
        await using var slow = await RawExchange.OpenAsync(address);
        await slow.SendAsync(Headers(ConfigurationImport, "application/json", 5_000_000, administrator: true, expectContinue: true, recentSecondFactor: true));
        await slow.ReadUntilAsync("HTTP/1.1 100 Continue\r\n\r\n", TimeSpan.FromSeconds(30));
        var started = Stopwatch.StartNew();
        await slow.SendAsync(Encoding.ASCII.GetBytes($"{{\"formatVersion\":1,\"sourceSite\":\"{Site}\",\"parts\":{{\"topology\":\""));
        slow.Trickle(new string('A', TrickleBytes), TrickleEvery);

        var answer = await slow.ReadAnswerAsync(TimeSpan.FromSeconds(60));
        var elapsed = started.Elapsed;

        TestContext.Current.SendDiagnosticMessage($"Trickled site configuration import answered after {elapsed.TotalSeconds:0.0} s: {answer}");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(40));
        answer.Should().StartWith("HTTP/1.1 408");
        answer.Should().Contain("Content-Type: application/problem+json");
        answer.Should().Contain("\"status\":408").And.Contain(SteadyUploadAttribute.Unreadable);
        answer.Should().NotContain("AAAA", "nothing that was sent is echoed").And.NotContain("Exception");
        configuration.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/api/v1/admin/alert-rules/backtest", 2_000_000, false, AlertRuleErrors.BacktestBodyTooLarge)]
    [InlineData("/api/v1/sites/DMO/validation/campaigns/0199a000-0000-7000-8000-000000009611/results/revisions", 5_000, true,
        Ariva.Api.Main.Controllers.OpsArea.Validation.ValidationResultsController.RecomputeBodyTooLarge)]
    public async Task Json_Should_Answer413WithItsOwnText_When_ABodyUnderAConcurrencyPolicyDeclaresMoreThanItsLimit(string path, long length,
        bool recentSecondFactor, string tooLarge)
    {
        // ARV-096b (security review L4): the backtest (the host's 1 MB) and the recompute (4 KB) refuse a declared length past their
        // limit before reading it, with their own fixed text and nothing of the request.
        var alertRules = new Mock<ISvcAlertRules>(MockBehavior.Strict);
        var results = new Mock<ISvcValidationResults>(MockBehavior.Strict);
        await using var app = KestrelMain(services =>
        {
            services.Replace(ServiceDescriptor.Scoped(_ => alertRules.Object));
            services.Replace(ServiceDescriptor.Scoped(_ => results.Object));
        });
        var address = ArivaHosts.Address(app);

        await using var exchange = await RawExchange.OpenAsync(address);
        await exchange.SendAsync(Headers(path, "application/json", length, administrator: true, expectContinue: true, recentSecondFactor));
        var answer = await exchange.ReadAnswerAsync(TimeSpan.FromSeconds(30));

        answer.Should().StartWith("HTTP/1.1 413");
        answer.Should().Contain("Content-Type: application/problem+json").And.Contain(tooLarge);
        answer.Should().NotContain("100 Continue", "the body is refused before it is asked for").And.NotContain("Exception");
        alertRules.VerifyNoOtherCalls();
        results.VerifyNoOtherCalls();
    }

    #endregion

    #region Helpers

    private static IArivaHost KestrelMain(Action<IServiceCollection> services) =>
        ArivaHosts.CreateMainOnKestrel(builder => builder.ConfigureTestServices(collection =>
        {
            TestAuthenticationHandler.Register(collection);
            FakeAdministration.Register(collection);
            collection.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
            services(collection);
        }));

    private static byte[] Headers(string path, string contentType, long contentLength, bool administrator, bool expectContinue, bool recentSecondFactor = false,
        string user = "steady-upload", bool signedIn = true, bool forged = false)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"POST {path} HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {contentLength}\r\n")
            .Append("Connection: close\r\n");
        if (signedIn)
            text.Append(CultureInfo.InvariantCulture, $"{TestAuthenticationHandler.UserHeader}: {user}\r\n")
                .Append(CultureInfo.InvariantCulture, $"Authorization: {TestAuthenticationHandler.Bearer}\r\n");
        else if (forged)
            // A bearer without its user: the test scheme does not authenticate it, as a forged or expired token is not.
            text.Append(CultureInfo.InvariantCulture, $"Authorization: {TestAuthenticationHandler.Bearer}\r\n");
        if (administrator)
            text.Append(CultureInfo.InvariantCulture, $"{TestAuthenticationHandler.RolesHeader}: {Ariva.Core.RoleCodes.SystemAdministrator}\r\n");
        if (recentSecondFactor)
            text.Append(CultureInfo.InvariantCulture, $"{TestAuthenticationHandler.MethodsHeader}: pwd,otp\r\n")
                .Append(CultureInfo.InvariantCulture, $"{TestAuthenticationHandler.AuthTimeHeader}: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\r\n");
        if (expectContinue)
            text.Append("Expect: 100-continue\r\n");
        return Encoding.ASCII.GetBytes(text.Append("\r\n").ToString());
    }

    private static byte[] FilePartHead() =>
        Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"mappings.csv\"\r\nContent-Type: text/csv\r\n\r\n");

    /// <summary>A whole small CSV preview on its own connection, by <paramref name="user"/>; the answer as received.</summary>
    private static async Task<string> FullCsvAsync(Uri address, string user = "steady-upload")
    {
        var body = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"mappings.csv\"\r\nContent-Type: text/csv\r\n\r\n" +
                                           $"system,externalCode,checkpoint,desk\r\nAman,D01,C1,D1\r\n--{Boundary}--\r\n");
        await using var exchange = await RawExchange.OpenAsync(address);
        await exchange.SendAsync([.. Headers(CsvPreview, "multipart/form-data; boundary=" + Boundary, body.Length, administrator: true, expectContinue: false, user: user), .. body]);
        return await exchange.ReadAnswerAsync(TimeSpan.FromSeconds(60));
    }

    private static string FirstLine(string answer) => answer.Length == 0 ? "(closed without an answer)" : answer.Split("\r\n")[0];

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException(what + ": not within 15 s");
            await Task.Delay(10, Ct);
        }
    }

    private static byte[] Form(string name, string value) =>
        Encoding.UTF8.GetBytes($"--{Boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n--{Boundary}--\r\n");

    private static async Task<bool> RunAsync(ResourceExecutingContext context, SteadyUploadAttribute filter = null)
    {
        var ran = false;
        await (filter ?? new SteadyUploadAttribute()).OnResourceExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ResourceExecutedContext(context, []));
        });
        return ran;
    }

    private static ResourceExecutingContext Context(byte[] body, string contentType = "multipart/form-data; boundary=" + Boundary, long? contentLength = -1, bool bindsBody = false)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = contentLength == -1 ? body.Length : contentLength;
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodySize { MaxRequestBodySize = 1024 * 1024 });
        var action = new ActionDescriptor
        {
            Parameters = bindsBody
                ? [new ParameterDescriptor { Name = "request", ParameterType = typeof(ImportSiteConfigurationRequest), BindingInfo = new BindingInfo { BindingSource = BindingSource.Body } }]
                : [new ParameterDescriptor { Name = "file", ParameterType = typeof(IFormFile), BindingInfo = new BindingInfo { BindingSource = BindingSource.FormFile } }]
        };
        return new ResourceExecutingContext(new ActionContext(http, new RouteData(), action), [], []);
    }

    private static ProblemDetails Problem(ResourceExecutingContext context, int status)
    {
        var answer = context.Result.Should().BeOfType<ObjectResult>().Subject;
        answer.StatusCode.Should().Be(status);
        answer.ContentTypes.Should().Contain("application/problem+json");
        return answer.Value.Should().BeOfType<ProblemDetails>().Subject;
    }

    private static List<RouteEndpoint> RouteEndpoints(IArivaHost app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    /// <summary>A controller action as <c>Controller.Action</c> (the controller's name without its suffix), or the endpoint's name.</summary>
    private static string Action(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action ? $"{action.ControllerName}.{action.ActionName}" : endpoint.DisplayName ?? string.Empty;

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }

    /// <summary>A request's minimum body rate; Kestrel's refuses any rate but none on HTTP/2 and HTTP/3.</summary>
    private sealed class BodyRate(bool refuses) : IHttpMinRequestBodyDataRateFeature
    {
        private Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate _rate;

        public Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate MinDataRate
        {
            get => _rate;
            set => _rate = refuses && value is not null ? throw new NotSupportedException("HTTP/2 and HTTP/3 take the connection's rate.") : value;
        }
    }

    private sealed class BodySize : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    /// <summary>A body Kestrel refused while it was read: too slow (408) or past the limit (413).</summary>
    private sealed class KestrelRefusedBody(int status) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw Refused();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Refused();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Refused();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private BadHttpRequestException Refused() => new("Kestrel refused the body.", status);
    }

    /// <summary>A body that can be read once, front to back, as the network gives it; counts its reads.</summary>
    private sealed class OnceOnlyBody(byte[] bytes) : Stream
    {
        private int _position;

        public int Reads { get; private set; }
        public bool ReadToTheEnd { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Take(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Take(Span<byte> buffer)
        {
            Reads++;
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            ReadToTheEnd |= count == 0;
            return count;
        }
    }

    /// <summary>
    /// One HTTP/1.1 exchange over a plain loopback socket, so the test sets every byte and its timing: an upload trickled in, or a
    /// whole one. The answer is read until the server closes the connection (every request here says <c>Connection: close</c>).
    /// </summary>
    private sealed class RawExchange : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly MemoryStream _received = new();
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        private RawExchange(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        public static async Task<RawExchange> OpenAsync(Uri address)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, address.Port, Ct);
            return new RawExchange(tcp);
        }

        public Task SendAsync(byte[] bytes) => _stream.WriteAsync(bytes, Ct).AsTask();

        /// <summary>Sends <paramref name="chunk"/> every <paramref name="every"/> in the background until the server stops reading or the exchange is disposed.</summary>
        public void Trickle(string chunk, TimeSpan every)
        {
            var bytes = Encoding.ASCII.GetBytes(chunk);
            var stop = _stop.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await _stream.WriteAsync(bytes, stop);
                        await Task.Delay(every, stop);
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // The server cut the body off (or the test is over): the trickle stops.
                }
            }, CancellationToken.None);
        }

        /// <summary>Reads until <paramref name="marker"/> has arrived and drops what came up to it.</summary>
        public async Task ReadUntilAsync(string marker, TimeSpan within)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(within);
            var buffer = new byte[4096];
            while (!Encoding.ASCII.GetString(_received.ToArray()).Contains(marker, StringComparison.Ordinal))
            {
                var read = await _stream.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                    throw new IOException($"The server closed the connection before \"{marker.Trim()}\": {Encoding.ASCII.GetString(_received.ToArray())}");
                _received.Write(buffer, 0, read);
            }

            var text = Encoding.ASCII.GetString(_received.ToArray());
            var rest = _received.ToArray()[(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            _received.SetLength(0);
            _received.Write(rest);
        }

        /// <summary>
        /// What the server has sent once <paramref name="marker"/> is in it, without waiting for the connection to close (ARV-265:
        /// Kestrel keeps one open a while to drain a body it refused unread); what came before a close, if it closes first.
        /// </summary>
        public async Task<string> ReadThroughAsync(string marker, TimeSpan within)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(within);
            var buffer = new byte[4096];
            while (!Encoding.UTF8.GetString(_received.ToArray()).Contains(marker, StringComparison.Ordinal))
            {
                var read = await _stream.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                    break;
                _received.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(_received.ToArray());
        }

        /// <summary>Everything the server sends until it closes the connection (empty when it closed without an answer).</summary>
        public async Task<string> ReadAnswerAsync(TimeSpan within)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(within);
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await _stream.ReadAsync(buffer, deadline.Token)) > 0)
                    _received.Write(buffer, 0, read);
            }
            catch (IOException)
            {
                // Reset rather than closed: what arrived before it is the answer.
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No answer within {within.TotalSeconds:0} s (the body was not cut off): {Encoding.UTF8.GetString(_received.ToArray())}");
            }

            return Encoding.UTF8.GetString(_received.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _stop.Dispose();
            _stream.Dispose();
            _tcp.Dispose();
            _received.Dispose();
        }
    }

    #endregion
}
