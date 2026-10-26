using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Logging;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Integration;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using Ariva.UnitTests.Logging.Fixtures;
using Ariva.UnitTests.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-091 (ASVS 5.0 V16.2.1, V16.3.2, V16.3.3; CWE-778, CWE-532): on real hosts, read back from the JSON lines the hosts
/// write, each refusal writes exactly one security event with its EventId: the authorization middleware's 401 and 403, the
/// rate limiter's 429, an invalid model, a body that is not strict JSON, a repeated query parameter, a NUL character in the
/// query, the body or a form field or file name of an upload otherwise invalid (ARV-096c: the NUL check runs first), a site outside the caller's or a value that is not a site code, and, through the generic
/// RequestRefused event, the refusals made elsewhere (an ended session, a pending account, a service's border-only 403, an
/// integration client's own limit). Each names the caller (CallerUserId and CallerSessionId, or anonymous; CallerClientId for
/// an integration client, CallerDeviceId for a device), the client address the trusted proxy names, the trace id, the method
/// and the endpoint's route pattern, and none of the refused input; the answers are the ones the hosts gave before. An
/// ordinary request's lines carry the same who and where, with the staff email masked and the desk code and track id
/// hashed; a line that names a target account keeps the target apart from the caller; a line written after the answer still
/// names the request's caller. The JSON file is for vm-local only, and no Serilog setting can add a sink.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SecurityEventLogTests : IDisposable
{
    #region Fields

    /// <summary>The trusted proxy the test connection comes from, and the client it names.</summary>
    private const string Proxy = "10.9.9.9";
    private const string Client = "203.0.113.7";

    /// <summary>Sent in the refused input; never in a log line.</summary>
    private const string Canary = "canary-91f3c7";
    private const string Marker = "<script>alert(1)</script>" + Canary;

    private const string IntegrationClientId = "ic_securityeventsprobeclient";

    private static readonly Guid AdminId = Guid.Parse("0199a000-0000-7000-8000-000000009101");
    private static readonly Guid PlainId = Guid.Parse("0199a000-0000-7000-8000-000000009102");
    private static readonly Guid SessionId = Guid.Parse("0199a000-0000-7000-8000-000000009103");
    private static readonly Guid RevokedSessionId = Guid.Parse("0199a000-0000-7000-8000-000000009105");
    private static readonly Guid TargetId = Guid.Parse("0199a000-0000-7000-8000-000000009106");
    private static readonly Guid TargetSessionId = Guid.Parse("0199a000-0000-7000-8000-000000009107");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-security-events-").FullName;

    #endregion

    #region Theory data

    /// <summary>Each refusal: name, the host's global limit (0 for the default), the expected status, event id, reason and caller.</summary>
    public static TheoryData<string, int, HttpStatusCode, int, string, string> Refusals => new()
    {
        { "no token", 0, HttpStatusCode.Unauthorized, SecurityEvents.AuthorizationChallengedId, SecurityEvents.NotAuthenticated, "anonymous" },
        { "no recent second factor", 0, HttpStatusCode.Unauthorized, SecurityEvents.AuthorizationChallengedId, SecurityEvents.SecondFactorRequired, "admin" },
        { "no permission", 0, HttpStatusCode.Forbidden, SecurityEvents.AuthorizationForbiddenId, SecurityEvents.PermissionMissing, "plain" },
        { "an invalid model", 0, HttpStatusCode.BadRequest, SecurityEvents.InvalidModelRefusedId, SecurityEvents.InvalidModel, "admin" },
        { "a body that is not strict JSON", 0, HttpStatusCode.BadRequest, SecurityEvents.StrictJsonRefusedId, SecurityEvents.NotStrictJson, "admin" },
        { "a repeated query parameter", 0, HttpStatusCode.BadRequest, SecurityEvents.RepeatedQueryRefusedId, SecurityEvents.RepeatedQuery, "admin" },
        { "a NUL character in the query", 0, HttpStatusCode.BadRequest, SecurityEvents.NulCharacterRefusedId, SecurityEvents.NulCharacter, "admin" },
        { "a NUL character in the body", 0, HttpStatusCode.BadRequest, SecurityEvents.NulCharacterRefusedId, SecurityEvents.NulCharacter, "admin" },
        { "a NUL character in a form field of an otherwise invalid upload", 0, HttpStatusCode.BadRequest, SecurityEvents.NulCharacterRefusedId, SecurityEvents.NulCharacter, "admin" },
        { "a NUL character in the file name of an otherwise invalid upload", 0, HttpStatusCode.BadRequest, SecurityEvents.NulCharacterRefusedId, SecurityEvents.NulCharacter, "admin" },
        { "a site outside the caller's", 0, HttpStatusCode.NotFound, SecurityEvents.SiteScopeRefusedId, SecurityEvents.OutsideSiteScope, "admin" },
        { "a value that is not a site code", 0, HttpStatusCode.NotFound, SecurityEvents.SiteScopeRefusedId, SecurityEvents.NotASiteCode, "admin" },
        { "the global rate limit", 1, HttpStatusCode.TooManyRequests, SecurityEvents.RateLimitRejectedId, null, "anonymous" },
        { "an ended session", 0, HttpStatusCode.Unauthorized, SecurityEvents.RequestRefusedId, SecurityEvents.CredentialRefused, "admin" },
        { "a pending account", 0, HttpStatusCode.Forbidden, SecurityEvents.RequestRefusedId, SecurityEvents.AccessRefused, "plain" },
        { "a border-only rule", 0, HttpStatusCode.Forbidden, SecurityEvents.RequestRefusedId, SecurityEvents.AccessRefused, "admin" }
    };

    #endregion

    #region Lifetime

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    #endregion

    #region Refusals on Ariva.Api.Main

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Refusal_Should_WriteOneSecurityEventWithWhoWhereAndWhy_When_MainRefusesTheRequest(
        string refusal, int globalLimit, HttpStatusCode status, int eventId, string reason, string caller)
    {
        var log = Path.Combine(_folder, "main.log");
        var session = refusal == "an ended session" ? RevokedSessionId : SessionId;
        var app = MainHost(log, globalLimit);
        HttpResponseMessage response;
        string body;
        await using (app)
        {
            using var client = app.CreateClient();
            if (globalLimit > 0)
                (await client.SendAsync(Request(HttpMethod.Get, "/fixture/log/ordinary", Token(app, "admin", secondFactor: true)), Ct)).Dispose();

            var token = Token(app, caller, secondFactor: refusal != "no recent second factor", session, pending: refusal == "a pending account");
            response = await client.SendAsync(RequestFor(refusal, token), Ct);
            body = await response.BodyAsync();
        }

        using (response)
            response.StatusCode.Should().Be(status, refusal);
        body.Should().NotContain(Canary, "the answer repeats nothing the caller sent");

        var security = Lines(log).Where(line => EventIdOf(line) is { } id && SecurityEvents.RequestEventIds.Contains(id)).ToList();
        security.Should().ContainSingle($"{refusal} is one refusal: one security event, none twice");
        var entry = Events(log, eventId).Should().ContainSingle(refusal).Subject;
        var properties = entry.GetProperty("Properties");
        entry.GetProperty("Level").GetString().Should().Be("Warning");
        entry.GetProperty("TraceId").GetString().Should().MatchRegex("^[0-9a-f]{32}$", "Serilog writes the request's trace id on the line");
        entry.GetProperty("SpanId").GetString().Should().MatchRegex("^[0-9a-f]{16}$");
        properties.GetProperty(RequestLogContext.CallerAddressProperty).GetString().Should().Be(Client, "the address the trusted proxy names");
        properties.GetProperty(RequestLogContext.CallerUserIdProperty).GetString().Should().Be(caller switch
        {
            "admin" => AdminId.ToString(),
            "plain" => PlainId.ToString(),
            _ => RequestLogContext.Anonymous
        });
        if (caller == "anonymous")
            properties.TryGetProperty(RequestLogContext.CallerSessionIdProperty, out _).Should().BeFalse();
        else
            properties.GetProperty(RequestLogContext.CallerSessionIdProperty).GetString().Should().Be(session.ToString());
        properties.GetProperty("Method").GetString().Should().NotBeNullOrEmpty();
        properties.GetProperty("Endpoint").GetString().Should().MatchRegex("^(fixture/log/|api/v1/admin/)", "the route pattern, not the path");
        properties.TryGetProperty("RequestPath", out _).Should().BeFalse("a request-level event names the route pattern, never the path");
        if (reason is not null)
            properties.GetProperty("Reason").GetString().Should().Be(reason);
        else
        {
            properties.GetProperty("Policy").GetString().Should().Be(SecurityEvents.GlobalPolicy);
            properties.GetProperty("Partition").GetString().Should().Be(SecurityEvents.AddressPartition);
        }

        if (eventId == SecurityEvents.RequestRefusedId)
            properties.GetProperty("Status").GetInt32().Should().Be((int)status);
        properties.GetProperty("EventId").GetProperty("Name").GetString().Should().StartWith("SecurityEvent.");
        File.ReadAllText(log).Should().NotContain(Canary, "no log line holds the refused input").And.NotContain("<script>");
    }

    [Fact]
    public async Task Forbidden_Should_NameThePermissionTheCallerLacks_When_ThePermissionIsMissing()
    {
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(log))
        {
            using var client = app.CreateClient();
            using var response = await client.SendAsync(RequestFor("no permission", Token(app, "plain", secondFactor: true)), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var properties = Events(log, SecurityEvents.AuthorizationForbiddenId).Single().GetProperty("Properties");
        properties.GetProperty("Requirement").GetString().Should().Be("Permission User.Search");
        properties.GetProperty("Endpoint").GetString().Should().Be("api/v1/admin/users");
        properties.GetProperty("Method").GetString().Should().Be("GET");
    }

    [Fact]
    public async Task InvalidModel_Should_LeaveTheRefusedRouteValueOut_When_ThePathHoldsIt()
    {
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(log))
        {
            using var client = app.CreateClient();
            using var response = await client.SendAsync(Request(HttpMethod.Get, "/fixture/binding/" + Canary, Token(app, "admin", secondFactor: true)), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var entry = Events(log, SecurityEvents.InvalidModelRefusedId).Should().ContainSingle().Subject;
        entry.GetRawText().Should().NotContain(Canary, "the path held the value that did not bind");
        entry.GetProperty("Properties").GetProperty("Endpoint").GetString().Should().Be("fixture/binding/{number}");
    }

    [Fact]
    public async Task OrdinaryRequest_Should_WriteLinesWithTheCallerAndProtectedValues_When_TheActionLogs()
    {
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(log))
        {
            using var client = app.CreateClient();
            using var response = await client.SendAsync(Request(HttpMethod.Get, "/fixture/log/ordinary", Token(app, "admin", secondFactor: true)), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var lines = Lines(log);
        var probe = lines.Should().ContainSingle(l => l.GetProperty("MessageTemplate").GetString() == LogProbeController.Template).Subject;
        var properties = probe.GetProperty("Properties");
        properties.GetProperty(RequestLogContext.CallerUserIdProperty).GetString().Should().Be(AdminId.ToString());
        properties.GetProperty(RequestLogContext.CallerSessionIdProperty).GetString().Should().Be(SessionId.ToString());
        properties.GetProperty(RequestLogContext.CallerAddressProperty).GetString().Should().Be(Client);
        probe.GetProperty("TraceId").GetString().Should().MatchRegex("^[0-9a-f]{32}$");
        properties.GetProperty("Email").GetString().Should().Be("p***@example.com");
        properties.GetProperty("DeskCode").GetString().Should().MatchRegex("^#[0-9a-f]{12}$");
        properties.GetProperty("TrackId").GetString().Should().MatchRegex("^#[0-9a-f]{12}$");
        probe.GetProperty("RenderedMessage").GetString().Should().Contain("p***@example.com").And.Contain(properties.GetProperty("DeskCode").GetString());
        properties.TryGetProperty("RequestPath", out _).Should().BeTrue("ordinary lines keep the hosting scope");
        var text = File.ReadAllText(log);
        text.Should().NotContain(LogProbeController.Email).And.NotContain(LogProbeController.DeskCode).And.NotContain(LogProbeController.TrackId);
        var duringRequests = lines.Where(l => l.GetProperty("Properties").TryGetProperty("RequestId", out var _)).ToList();
        duringRequests.Should().NotBeEmpty();
        duringRequests.Select(l => l.GetProperty("Properties").TryGetProperty(RequestLogContext.CallerAddressProperty, out var _))
            .Should().AllBeEquivalentTo(true, "every line written during a request names the client");
    }

    [Fact]
    public async Task AdministratorLine_Should_KeepTheTargetApartFromTheCaller_When_ItNamesAnotherAccountAndSession()
    {
        var log = Path.Combine(_folder, "main.log");
        await using (var app = MainHost(log))
        {
            using var client = app.CreateClient();
            using var response = await client.SendAsync(
                Request(HttpMethod.Post, $"/fixture/log/accounts/{TargetId}/sessions/{TargetSessionId}", Token(app, "admin", secondFactor: true)), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var line = Lines(log).Should().ContainSingle(l => l.GetProperty("MessageTemplate").GetString() == LogProbeController.AccountTemplate).Subject;
        var properties = line.GetProperty("Properties");
        properties.GetProperty("UserId").GetString().Should().Be(TargetId.ToString(), "the template's own account is the target");
        properties.GetProperty("SessionId").GetString().Should().Be(TargetSessionId.ToString(), "and its session the target's");
        properties.GetProperty(RequestLogContext.CallerUserIdProperty).GetString().Should().Be(AdminId.ToString(), "the caller is the administrator");
        properties.GetProperty(RequestLogContext.CallerSessionIdProperty).GetString().Should().Be(SessionId.ToString(), "in the administrator's session");
        properties.GetProperty(RequestLogContext.CallerAddressProperty).GetString().Should().Be(Client, "from the administrator's address");
    }

    [Fact]
    public async Task LineWrittenAfterTheAnswer_Should_NameTheRequestsCaller_When_AnotherRequestHasRunSince()
    {
        var log = Path.Combine(_folder, "main.log");
        LogProbeController.Released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LogProbeController.Written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var app = MainHost(log))
        {
            using var client = app.CreateClient();
            using (var answered = await client.SendAsync(Request(HttpMethod.Get, "/fixture/log/later", Token(app, "admin", secondFactor: true)), Ct))
                answered.StatusCode.Should().Be(HttpStatusCode.OK, "the answer is given before the line is written");
            using (var other = await client.SendAsync(Request(HttpMethod.Get, "/fixture/log/ordinary", Token(app, "plain", secondFactor: true), forwardedFor: "198.51.100.4"), Ct))
                other.StatusCode.Should().Be(HttpStatusCode.Forbidden, "another caller's request runs in between");

            LogProbeController.Released.TrySetResult();
            await LogProbeController.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        var later = Lines(log).Should().ContainSingle(l => l.GetProperty("MessageTemplate").GetString() == LogProbeController.LaterTemplate).Subject;
        var properties = later.GetProperty("Properties");
        properties.GetProperty(RequestLogContext.CallerUserIdProperty).GetString().Should().Be(AdminId.ToString(), "the values were copied, never read again");
        properties.GetProperty(RequestLogContext.CallerAddressProperty).GetString().Should().Be(Client);
    }

    #endregion

    #region Every Backplane host

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Challenge_Should_WriteTheSecurityEventWithTheClientAddress_When_AnyHostRefusesAnAnonymousCaller(string host)
    {
        var log = Path.Combine(_folder, host + ".log");
        await using (var app = ArivaHosts.Create(host, configure: builder => Logged(builder, log)))
        {
            using var client = app.CreateClient();
            using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/not-a-route?q=" + Uri.EscapeDataString(Marker), token: null), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer", "the answer is the default deny challenge, as before");
        }

        var properties = Events(log, SecurityEvents.AuthorizationChallengedId).Should().ContainSingle(host).Subject.GetProperty("Properties");
        properties.GetProperty(RequestLogContext.CallerAddressProperty).GetString().Should().Be(Client, host);
        properties.GetProperty(RequestLogContext.CallerUserIdProperty).GetString().Should().Be(RequestLogContext.Anonymous, host);
        properties.GetProperty("Endpoint").GetString().Should().Be(SecurityEventLog.NoEndpoint, host);
        Events(log, SecurityEvents.RequestRefusedId).Should().BeEmpty($"{host}: the challenge's own event, not a second one");
        File.ReadAllText(log).Should().NotContain(Canary, host);
    }

    #endregion

    #region Devices on Ariva.Api.Ingest

    [Fact]
    public async Task DeviceRefusals_Should_NameTheDeviceAndThePolicy_When_ADeviceRepeatsAParameterAndThenFloods()
    {
        var log = Path.Combine(_folder, "ingest.log");
        await using (var app = ArivaHosts.Create(ArivaHosts.Ingest, configure: builder =>
        {
            Logged(builder, log);
            builder.UseSetting("Security:RateLimiting:Device:PermitLimit", "1");
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, OneDeviceGateway>()));
        }))
        {
            using var client = app.CreateClient();
            using var repeated = await client.SendAsync(AsDevice("/api/v1/ingest/device?zone=" + Canary + "&zone=b"), Ct);
            using var flooded = await client.SendAsync(AsDevice("/api/v1/ingest/device?zone=Snake%20A"), Ct);
            repeated.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            flooded.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            flooded.Headers.RetryAfter.Should().NotBeNull("the 429 answer is unchanged");
        }

        var refused = Events(log, SecurityEvents.RepeatedQueryRefusedId).Should().ContainSingle().Subject.GetProperty("Properties");
        refused.GetProperty(RequestLogContext.CallerDeviceIdProperty).GetString().Should().Be(OneDeviceGateway.DeviceId.ToString());
        refused.TryGetProperty(RequestLogContext.CallerUserIdProperty, out _).Should().BeFalse("a device is not a user");
        var limited = Events(log, SecurityEvents.RateLimitRejectedId).Should().ContainSingle().Subject.GetProperty("Properties");
        limited.GetProperty("Policy").GetString().Should().Be("device");
        limited.GetProperty("Partition").GetString().Should().Be(SecurityEvents.DeviceCredentialPartition);
        limited.GetProperty(RequestLogContext.CallerAddressProperty).GetString().Should().Be(Client);
        var text = File.ReadAllText(log);
        text.Contains(OneDeviceGateway.Device.Credential, StringComparison.Ordinal).Should().BeFalse("the log never holds the device credential");
        text.Should().NotContain(Canary).And.NotContain(OneDeviceGateway.Device.Hash);
    }

    #endregion

    #region Integration clients on Ariva.Api.Integration

    [Fact]
    public async Task IntegrationRefusals_Should_NameTheClient_When_ItLacksAScopeAndThenPassesItsOwnLimit()
    {
        var log = Path.Combine(_folder, "integration.log");
        string token;
        await using (var app = ArivaHosts.Create(ArivaHosts.Integration, configure: builder =>
        {
            Logged(builder, log);
            builder.UseSetting("Security:RateLimiting:IntegrationClient:PermitLimit", "2");
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<ISvcIntegrationAuth, OneIntegrationClient>()));
        }))
        {
            using var client = app.CreateClient();
            (token, _) = app.Services.GetRequiredService<IntegrationTokenIssuer>().Issue(IntegrationClientId, SessionId, 1, ["flights:write"], ["DMO"]);
            using var scope = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/integration/sites/DMO/immigration/check?probe=" + Canary, token), Ct);
            using var allowed = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/integration/sites/DMO/flights/check", token), Ct);
            using var limited = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/integration/sites/DMO/flights/check?probe=" + Canary, token), Ct);
            scope.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the client's own limit, counted in the frozen integration middleware");
            limited.Headers.RetryAfter.Should().NotBeNull();
        }

        var forbidden = Events(log, SecurityEvents.AuthorizationForbiddenId).Should().ContainSingle().Subject.GetProperty("Properties");
        forbidden.GetProperty(RequestLogContext.CallerClientIdProperty).GetString().Should().Be(IntegrationClientId);
        forbidden.GetProperty(RequestLogContext.CallerSessionIdProperty).GetString().Should().Be(SessionId.ToString());
        forbidden.GetProperty("Reason").GetString().Should().Be(SecurityEvents.ScopeMissing);
        forbidden.GetProperty("Requirement").GetString().Should().Be("Scope immigration:write");
        var refused = Events(log, SecurityEvents.RequestRefusedId).Should().ContainSingle().Subject.GetProperty("Properties");
        refused.GetProperty("Status").GetInt32().Should().Be(429);
        refused.GetProperty("Reason").GetString().Should().Be(SecurityEvents.LimitReached);
        refused.GetProperty(RequestLogContext.CallerClientIdProperty).GetString().Should().Be(IntegrationClientId, "the checked client, taken when the request ends");
        refused.GetProperty("Endpoint").GetString().Should().Be("api/v1/integration/sites/{siteCode}/flights/check");
        var logged = File.ReadAllText(log);
        logged.Contains(token, StringComparison.Ordinal).Should().BeFalse("the log never holds the bearer token");
        logged.Should().NotContain(Canary);
    }

    #endregion

    #region Log destinations and levels

    [Theory]
    [InlineData(ArivaEnvironment.K8sDev)]
    [InlineData(ArivaEnvironment.K8sDemo)]
    [InlineData(ArivaEnvironment.K8sPrd)]
    public void Configure_Should_RefuseTheFileSink_When_TheEnvironmentIsNotVmLocal(string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { [ArivaLogging.FileSetting] = Path.Combine(_folder, "pod.log") })
            .Build();

        var configure = () => ArivaLogging.Configure(new Serilog.LoggerConfiguration(), configuration, environment);

        configure.Should().Throw<InvalidOperationException>().WithMessage("*LogFile:Path is for vm-local only*");
        File.Exists(Path.Combine(_folder, "pod.log")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.AllHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_TheFileSinkIsSetOutsideVmLocal(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sDev, builder => builder.UseSetting(ArivaLogging.FileSetting, Path.Combine(_folder, host + ".log")));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("*LogFile:Path is for vm-local only*");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_SerilogConfigurationAddsAFileSink(string host)
    {
        var file = Path.Combine(_folder, host + "-configured.log");
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sDev, builder => builder
            .UseSetting("Serilog:Using:0", "Serilog.Sinks.File")
            .UseSetting("Serilog:WriteTo:0:Name", "File")
            .UseSetting("Serilog:WriteTo:0:Args:path", file));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("*Serilog:*is not read*");
        File.Exists(file).Should().BeFalse("no file was opened");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_SerilogConfigurationSendsToLokiInClearInProduction(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder
            .UseSetting("Serilog:WriteTo:0:Name", "GrafanaLoki")
            .UseSetting("Serilog:WriteTo:0:Args:uri", "http://loki.example:3100"));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("*Serilog:WriteTo is not read*");
    }

    #endregion

    #region Helpers

    private static void Logged(IWebHostBuilder builder, string log) => builder
        .UseSetting(ArivaLogging.FileSetting, log)
        .UseSetting("Security:ForwardedHeaders:KnownProxies:0", Proxy)
        .ConfigureTestServices(services => services.AddSingleton<IStartupFilter, ProxyConnection>());

    private static IArivaHost MainHost(string log, int globalLimit = 0) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            Logged(builder, log);
            if (globalLimit > 0)
                builder.UseSetting("Security:RateLimiting:Global:PermitLimit", globalLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services =>
            {
                var sessions = new FakeSessionValidator { [RevokedSessionId] = SessionState.Revoked };
                services.Replace(ServiceDescriptor.Singleton<ISessionValidator>(sessions));
                services.Replace(ServiceDescriptor.Scoped<IPermissionResolver>(_ => new Grants()));
                services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new OneSiteScope()));
                services.Replace(ServiceDescriptor.Scoped<ISvcAlertRules, BorderOnlyRules>());
                // ARV-096c: the upload is refused before its action, so the service is never called.
                services.Replace(ServiceDescriptor.Scoped(_ => new Mock<ISvcFlightSchedules>(MockBehavior.Strict).Object));
                FakeAdministration.Register(services);
                BindingProbeController.Mount(services);
                LogProbeController.Mount(services);
            });
        });

    private static string Token(IArivaHost app, string caller, bool secondFactor, Guid? session = null, bool pending = false) => caller switch
    {
        "admin" or "plain" => app.Services.GetRequiredService<AccessTokenIssuer>().Issue(caller == "admin" ? AdminId : PlainId, caller + ".one", session ?? SessionId,
            Guid.CreateVersion7(), DateTime.UtcNow, secondFactor ? ["pwd", "otp"] : ["pwd"], pending),
        _ => null
    };

    private static HttpRequestMessage RequestFor(string refusal, string token) => refusal switch
    {
        "no token" => Request(HttpMethod.Get, "/api/v1/admin/users?text=" + Uri.EscapeDataString(Marker), null),
        "no recent second factor" => Request(HttpMethod.Post, "/api/v1/admin/users", token, $$"""{"userName":"{{Canary}}"}"""),
        "no permission" or "an ended session" or "a pending account" => Request(HttpMethod.Get, "/api/v1/admin/users?text=" + Uri.EscapeDataString(Marker), token),
        "an invalid model" => Request(HttpMethod.Post, "/api/v1/admin/users", token, $$"""{"userName":"ok","email":"{{Marker.Replace("\"", string.Empty, StringComparison.Ordinal)}}"}"""),
        "a body that is not strict JSON" => Request(HttpMethod.Post, "/api/v1/admin/users", token, $$"""{"userName":"ok","{{Canary}}<script>":1}"""),
        "a repeated query parameter" => Request(HttpMethod.Get, "/api/v1/admin/users?text=" + Uri.EscapeDataString(Marker) + "&text=" + Canary, token),
        "a NUL character in the query" => Request(HttpMethod.Get, "/api/v1/admin/users?text=" + Canary + "%00", token),
        "a NUL character in the body" => Request(HttpMethod.Post, "/api/v1/admin/users", token, $$"""{"userName":"{{Canary}}\u0000"}"""),
        // ARV-096c: horizonDays does not bind (an invalid model), so before the NUL check took its own order the invalid model filter
        // answered first and the upload was logged as an invalid model.
        "a NUL character in a form field of an otherwise invalid upload" => Upload(token, note: Canary + "\0", fileName: "schedule.ssim"),
        "a NUL character in the file name of an otherwise invalid upload" => Upload(token, note: "ok", fileName: "schedule" + Canary + "\0.ssim"),
        "a site outside the caller's" => Request(HttpMethod.Get, "/fixture/log/sites/BEY?note=" + Uri.EscapeDataString(Marker), token),
        "a value that is not a site code" => Request(HttpMethod.Get, "/fixture/log/sites/B_EY?note=" + Uri.EscapeDataString(Marker), token),
        "a border-only rule" => Request(HttpMethod.Get, $"/api/v1/admin/alert-rules/{Guid.CreateVersion7()}?note={Uri.EscapeDataString(Marker)}", token),
        _ => Request(HttpMethod.Get, "/fixture/log/ordinary?note=" + Uri.EscapeDataString(Marker), null)
    };

    /// <summary>A flight schedule upload whose horizonDays is not a number, with the note and the file name given.</summary>
    private static HttpRequestMessage Upload(string token, string note, string fileName)
    {
        const string boundary = "arivanul";
        var body = $"--{boundary}\r\nContent-Disposition: form-data; name=\"horizonDays\"\r\n\r\nsoon\r\n" +
                   $"--{boundary}\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\n{note}\r\n" +
                   $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\nContent-Type: application/octet-stream\r\n\r\nx\r\n" +
                   $"--{boundary}--\r\n";
        var request = Request(HttpMethod.Post, "/api/v1/admin/sites/DMO/flight-schedules/preview", token);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        return request;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token, string json = null, string forwardedFor = Client)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage AsDevice(string path)
    {
        var request = Request(HttpMethod.Get, path, null);
        request.Headers.Add(Ariva.Api.Common.Security.DeviceAuthentication.KeyHeader, OneDeviceGateway.Device.Credential);
        return request;
    }

    private static List<JsonElement> Lines(string log)
    {
        File.Exists(log).Should().BeTrue("the host writes its JSON lines to LogFile:Path in vm-local");
        return File.ReadAllLines(log).Where(line => line.Length > 0).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToList();
    }

    private static int? EventIdOf(JsonElement line) =>
        line.GetProperty("Properties").TryGetProperty("EventId", out var id) && id.TryGetProperty("Id", out var value) ? value.GetInt32() : null;

    private static List<JsonElement> Events(string log, int eventId) => Lines(log).Where(line => EventIdOf(line) == eventId).ToList();

    #endregion

    #region Fixtures

    /// <summary>Every request arrives from the trusted proxy, which TestServer cannot say.</summary>
    private sealed class ProxyConnection : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(Proxy);
                return nextMiddleware();
            });
            next(app);
        };
    }

    /// <summary>The admin holds every permission; anyone else none.</summary>
    private sealed class Grants : IPermissionResolver
    {
        public Task<IReadOnlySet<Permission>> GetPermissionsAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Permission>>(user.FindFirst(ArivaClaims.Subject)?.Value == AdminId.ToString()
                ? new HashSet<Permission>(Global.Defaults.Permissions.All.Values)
                : new HashSet<Permission>());
    }

    /// <summary>Every caller may reach DMO only.</summary>
    private sealed class OneSiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new SiteAccess(false, new HashSet<string>(StringComparer.Ordinal) { "DMO" }));
    }

    /// <summary>Every rule is on a border-only metric the caller may not see: the service's own 403 (ARV-181b2).</summary>
    private sealed class BorderOnlyRules : ISvcAlertRules
    {
        public Task<Fluentx.Result<PageViewModel<AlertRuleViewModel>>> SearchAsync(AlertRuleCriteria criteria, CancellationToken ct = default) => Refused<PageViewModel<AlertRuleViewModel>>();

        public Task<Fluentx.Result<AlertRuleViewModel>> GetAsync(Guid id, CancellationToken ct = default) => Refused<AlertRuleViewModel>();

        public Task<Fluentx.Result<AlertRuleViewModel>> CreateAsync(AlertRuleRequest request, CancellationToken ct = default) => Refused<AlertRuleViewModel>();

        public Task<Fluentx.Result<AlertRuleViewModel>> UpdateAsync(Guid id, AlertRuleRequest request, CancellationToken ct = default) => Refused<AlertRuleViewModel>();

        public Task<Fluentx.Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default) => Refused<bool>();

        public Task<Fluentx.Result<AlertBacktestViewModel>> BacktestAsync(AlertBacktestRequest request, CancellationToken ct = default) => Refused<AlertBacktestViewModel>();

        private static Task<Fluentx.Result<T>> Refused<T>() => Task.FromResult(Fluentx.Result.Error<T>(AlertRuleErrors.BorderOnly));
    }

    /// <summary>One integration client, allowed flights:write at DMO; its calls are recorded nowhere.</summary>
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

    /// <summary>One known device.</summary>
    private sealed class OneDeviceGateway : ISvcDeviceGateway
    {
        public static readonly Guid DeviceId = Guid.Parse("0199a000-0000-7000-8000-000000009104");
        public static readonly DeviceCredentials.Issued Device = DeviceCredentials.New();

        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult(
            prefix == Device.Prefix ? new DeviceCredentialRecord(DeviceId, "S-17", "DMO", "Snake A", "Online", Device.Hash, [], null) : null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(Fluentx.Result.Error<DeviceZoneViewModel>("not used"));
    }

    #endregion
}
