using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Hosting;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Integration;
using Ariva.Core.Services.Integration;
using Ariva.Infra.Integration;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-096b (CWE-400, ASVS 5.0 V15.2.2): the request path of the hosts other than Ariva.Api.Main. Each has the default request timeout
/// (Ariva.Api.Cronz's TickerQ dashboard is a branch of the pipeline before it); Ariva.Api.Integration runs it before its call audit, so a
/// batch whose body stops arriving is answered 503 at the default and frees its permit, and asks its batches and AIDX messages to arrive
/// at the uploads' rate; the token exchange keeps no timeout while the login freeze lasts. A sensor push sets its rate over HTTP/1.x only.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class HostRequestPathTests
{
    private const string IntegrationClientId = "ic_requestpathprobeclient";
    private static readonly Guid SessionId = Guid.Parse("0199a000-0000-7000-8000-000000009601");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Request timeouts on every host

    [Theory]
    [InlineData(ArivaHosts.Ingest)]
    [InlineData(ArivaHosts.Stream)]
    [InlineData(ArivaHosts.Cronz)]
    [InlineData(ArivaHosts.Integration)]
    public async Task Options_Should_HoldTheDefault_When_TheHostIsNotMain(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var options = app.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        options.DefaultPolicy.Should().NotBeNull($"{host}: every endpoint without its own policy gets the default");
        options.DefaultPolicy.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        options.DefaultPolicy.TimeoutStatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Endpoints_Should_KeepNoTimeoutOnlyForTheTokenExchange_When_IntegrationMapsThem()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Integration);
        app.CreateClient().Dispose();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null).ToList();
        var exempt = endpoints.Where(e => e.Metadata.GetMetadata<DisableRequestTimeoutAttribute>() is not null).ToList();

        exempt.Select(e => e.Metadata.GetMetadata<ControllerActionDescriptor>().ControllerName).Should().OnlyContain(name => name == "Auth",
            "the login freeze keeps the token exchange as it was; every other endpoint has the default");
        exempt.Should().NotBeEmpty();
        endpoints.Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName is { } policy && SteadyIntegrationBodies.Policies.Contains(policy))
            .Should().NotBeEmpty().And.OnlyContain(e => e.Metadata.GetMetadata<DisableRequestTimeoutAttribute>() == null);
        var aidx = endpoints.Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitingExtensions.IntegrationAidxPolicy).ToList();
        aidx.Should().NotBeEmpty().And.OnlyContain(e => e.Metadata.GetMetadata<RequestTimeoutAttribute>().PolicyName == RequestTimeoutsExtensions.TransferPolicy,
            "5 MB at the slowest rate allowed takes about 170 s, past the default");
        endpoints.Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitingExtensions.IntegrationBatchPolicy)
            .Should().NotBeEmpty().And.OnlyContain(e => e.Metadata.GetMetadata<RequestTimeoutAttribute>() == null, "a 1 MB batch is in within about 40 s");
    }

    [Fact]
    public async Task Batch_Should_Answer503AndFreeItsPermit_When_ItsBodyStopsArrivingPastTheDefault()
    {
        // The call audit reads the body before MVC with the request's token, holding the batch policy's permit; one permit here, so
        // a second batch would wait or be refused while the first held it. The default (5 s here) cancels the read.
        await using var app = ArivaHosts.Create(ArivaHosts.Integration, configure: builder =>
        {
            builder.UseSetting("Security:RequestTimeouts:DefaultSeconds", "5");
            builder.UseSetting("Security:RateLimiting:IntegrationBatch:PermitLimit", "1");
            builder.UseSetting("Security:RateLimiting:IntegrationBatch:QueueLimit", "0");
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<ISvcIntegrationAuth, OneIntegrationClient>()));
        });
        using var client = app.CreateClient();
        var (token, _) = app.Services.GetRequiredService<IntegrationTokenIssuer>().Issue(IntegrationClientId, SessionId, 1, ["flights:write"], ["DMO"]);

        using var stalled = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integration/sites/DMO/flights/batch") { Content = new StalledContent() };
        stalled.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var watch = Stopwatch.StartNew();
        using var answer = await client.SendAsync(stalled, Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        watch.Stop();

        using var next = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integration/sites/DMO/flights/batch") { Content = new StringContent("{", Encoding.UTF8, "application/json") };
        next.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var after = await client.SendAsync(next, Ct);

        answer.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the default request timeout ends the audit's read");
        answer.Headers.RetryAfter.Should().NotBeNull();
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4)).And.BeLessThan(TimeSpan.FromSeconds(30));
        after.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "the stalled batch gave its permit back");
    }

    #endregion

    #region Concurrency slots

    [Theory]
    [InlineData("/api/v1/integration/sites/DMO/flights/batch", "IntegrationBatch")]
    [InlineData("/api/v1/integration/sites/DMO/aodb/aidx", "IntegrationAidx")]
    public async Task IntegrationPolicies_Should_GiveNoSlotToACallWithoutCredentials_When_TheirPermitIsHeld(string path, string limits)
    {
        // ARV-229a (the ARV-096b review, I4): one permit, none waiting, held by a signed-in call whose body stalls. A call that sends
        // no credentials is refused 401 at authentication at once, never 429 for a slot it would not use; anything that sends any (a
        // forged token too) is still counted, so the policy keeps bounding the bodies the call audit reads.
        await using var app = ArivaHosts.Create(ArivaHosts.Integration, configure: builder =>
        {
            builder.UseSetting($"Security:RateLimiting:{limits}:PermitLimit", "1");
            builder.UseSetting($"Security:RateLimiting:{limits}:QueueLimit", "0");
            builder.UseSetting("Security:RateLimiting:IntegrationClient:PermitLimit", "10000");
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<ISvcIntegrationAuth, OneIntegrationClient>()));
        });
        using var client = app.CreateClient();
        var (token, _) = app.Services.GetRequiredService<IntegrationTokenIssuer>().Issue(IntegrationClientId, SessionId, 1, ["flights:write"], ["DMO"]);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        HttpRequestMessage stalled = null;
        Task<HttpResponseMessage> holding = null;

        // The stalled call holds the permit once a signed-in call beside it is refused 429. A probe that reaches the limiter first takes
        // the permit for a moment, and the stalled call is the one refused: it has ended then, and another is sent.
        var probe = HttpStatusCode.OK;
        for (var attempt = 0; attempt < 200 && probe != HttpStatusCode.TooManyRequests; attempt++)
        {
            if (holding is null || holding.IsCompleted)
            {
                if (holding is not null)
                    (await holding).Dispose();
                stalled?.Dispose();
                stalled = Post(path, $"Bearer {token}", new StalledContent());
                holding = client.SendAsync(stalled, stop.Token);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Ct);
            using var signedIn = Post(path, $"Bearer {token}", new StringContent("{", Encoding.UTF8, "application/json"));
            using var answer = await client.SendAsync(signedIn, Ct);
            probe = answer.StatusCode;
        }

        using var noHeader = Post(path, null, new StringContent("[]", Encoding.UTF8, "application/json"));
        using var withoutCredentials = await client.SendAsync(noHeader, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var blankHeader = Post(path, "   ", new StringContent("[]", Encoding.UTF8, "application/json"));
        using var blank = await client.SendAsync(blankHeader, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var forgedHeader = Post(path, "Bearer forged", new StringContent("[]", Encoding.UTF8, "application/json"));
        using var forged = await client.SendAsync(forgedHeader, Ct);
        using var stillHeldRequest = Post(path, $"Bearer {token}", new StringContent("[]", Encoding.UTF8, "application/json"));
        using var stillHeld = await client.SendAsync(stillHeldRequest, Ct);
        var heldThroughout = !holding.IsCompleted;
        await stop.CancelAsync();
        try
        {
            using var _ = await holding;
        }
        catch (OperationCanceledException)
        {
            // The stalled call is ended by the test.
        }
        finally
        {
            stalled.Dispose();
        }

        probe.Should().Be(HttpStatusCode.TooManyRequests, $"{path}: the stalled call holds the policy's one permit");
        heldThroughout.Should().BeTrue($"{path}: the stalled call was still waiting for its body");
        withoutCredentials.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path}: a call without credentials takes no slot");
        blank.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path}: a blank Authorization header is no credential");
        forged.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, $"{path}: a forged token is counted, for the moment its authentication takes at least");
        forged.Headers.RetryAfter.Should().NotBeNull();
        stillHeld.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, $"{path}: the permit was held throughout");
    }

    private static HttpRequestMessage Post(string path, string authorization, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    #endregion

    #region Body rates

    [Theory]
    [InlineData(RateLimitingExtensions.IntegrationBatchPolicy, "HTTP/1.1", true)]
    [InlineData(RateLimitingExtensions.IntegrationAidxPolicy, "HTTP/1.1", true)]
    [InlineData(RateLimitingExtensions.IntegrationBatchPolicy, "HTTP/2", false)]
    [InlineData(RateLimitingExtensions.IntegrationAuthPolicy, "HTTP/1.1", false)]
    [InlineData(null, "HTTP/1.1", false)]
    public async Task SteadyIntegrationBodies_Should_SetTheUploadsRate_When_TheEndpointIsABatchOrAnAidxMessageOverHttp1(string policy, string protocol, bool set)
    {
        var context = new DefaultHttpContext { Request = { Protocol = protocol } };
        var rate = new BodyRateFeature(refuses: protocol != "HTTP/1.1");
        context.Features.Set<IHttpMinRequestBodyDataRateFeature>(rate);
        var metadata = policy is null ? new EndpointMetadataCollection() : new EndpointMetadataCollection(new EnableRateLimitingAttribute(policy));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "probe"));
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSteadyIntegrationBodies();
        var reached = false;
        app.Run(_ => { reached = true; return Task.CompletedTask; });

        await app.Build()(context);

        reached.Should().BeTrue();
        rate.Stored.Should().Be(set ? SteadyUploadAttribute.MinimumRate : null);
    }

    [Theory]
    [InlineData("HTTP/1.1", true)]
    [InlineData("HTTP/1.0", true)]
    [InlineData("HTTP/2", false)]
    [InlineData("HTTP/3", false)]
    public void RequireRate_Should_SetTheRateOverHttp1AndLeaveOthersAlone_When_ASensorPushes(string protocol, bool set)
    {
        // Kestrel refuses a per-request rate on HTTP/2 and HTTP/3 (the fake throws as it does); the push set it on any protocol.
        var context = new DefaultHttpContext { Request = { Protocol = protocol } };
        var rate = new BodyRateFeature(refuses: !set);
        context.Features.Set<IHttpMinRequestBodyDataRateFeature>(rate);

        var push = () => SteadyUploadAttribute.RequireRate(context, Ariva.Api.Ingest.Controllers.PushController.PushRate);

        push.Should().NotThrow();
        rate.Stored.Should().Be(set ? Ariva.Api.Ingest.Controllers.PushController.PushRate : null);
        Ariva.Api.Ingest.Controllers.PushController.PushRate.BytesPerSecond.Should().Be(1024);
    }

    #endregion

    #region Fakes

    /// <summary>A request body that sends nothing until the request is cancelled.</summary>
    private sealed class StalledContent : HttpContent
    {
        public StalledContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("[{"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>One known integration client, allowed flights:write on DMO.</summary>
    private sealed class OneIntegrationClient : ISvcIntegrationAuth
    {
        public Task<Fluentx.Result<IntegrationTokenViewModel>> ExchangeAsync(IntegrationTokenRequest request, IPAddress remote, CancellationToken ct = default) =>
            Task.FromResult(Fluentx.Result.Error<IntegrationTokenViewModel>(IntegrationErrors.InvalidClient));

        public Task<IntegrationCallCheck> CheckCallAsync(string clientId, int tokenVersion, IPAddress remote, string totpCode, CancellationToken ct = default) =>
            Task.FromResult(clientId == IntegrationClientId
                ? new IntegrationCallCheck(new IntegrationCallerViewModel(IntegrationClientId, "Probe AODB", ["flights:write"], ["DMO"], false), null)
                : new IntegrationCallCheck(null, "unknown client"));

        public Task RecordCallAsync(IntegrationCallRecord call, CancellationToken ct = default) => Task.CompletedTask;
    }

    #endregion
}
