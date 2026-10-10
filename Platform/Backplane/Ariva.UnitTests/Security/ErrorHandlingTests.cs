using System.Net;
using System.Text;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Hosting;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-209: error answers are ProblemDetails without stack traces, exception names or messages outside vm-local.
/// The authenticated test scheme lets the requests through default deny so the genuine 404, 405 and 500 answers
/// can be inspected; anonymous callers get 401 for all of them (see DefaultDenyTests).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ErrorHandlingTests
{
    #region Theory data

    public static TheoryData<string> NonLocalEnvironments => new() { ArivaEnvironment.K8sDev, ArivaEnvironment.K8sDemo, ArivaEnvironment.K8sPrd };

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(NonLocalEnvironments))]
    public async Task Get_Should_Return500WithoutExceptionDetails_When_EnvironmentIsNotVmLocal(string environment)
    {
        await using var app = CreateAuthenticatedHost(environment);
        using var client = CreateUserClient(app);

        using var response = await client.GetAsync(ThrowingStartupFilter.Path, TestContext.Current.CancellationToken);
        var body = await response.BodyAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ShouldBeProblemWithoutLeaks(response, body, 500);
        body.Should().NotContain(ThrowingStartupFilter.SecretMessage);
        HttpResponses.ParseProblem(body).TryGetProperty("exception", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Get_Should_Return500WithExceptionDetails_When_EnvironmentIsVmLocal()
    {
        await using var app = CreateAuthenticatedHost(ArivaEnvironment.VmLocal);
        using var client = CreateUserClient(app);

        using var response = await client.GetAsync(ThrowingStartupFilter.Path, TestContext.Current.CancellationToken);
        var problem = HttpResponses.ParseProblem(await response.BodyAsync());

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        problem.GetProperty("detail").GetString().Should().Be(ThrowingStartupFilter.SecretMessage);
        problem.GetProperty("exception").GetString().Should().Contain(nameof(InvalidOperationException));
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Get_Should_Return404ProblemDetails_When_RouteIsUnknownAndCallerIsAuthenticated(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder.ConfigureTestServices(TestAuthenticationHandler.Register));
        using var client = CreateUserClient(app);

        using var response = await client.GetAsync("/api/v1/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ShouldBeProblemWithoutLeaks(response, await response.BodyAsync(), 404);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Delete_Should_Return405ProblemDetails_When_MethodIsNotAllowedAndCallerIsAuthenticated(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder.ConfigureTestServices(TestAuthenticationHandler.Register));
        using var client = CreateUserClient(app);

        using var response = await client.DeleteAsync("/health/liveness", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().Contain("GET");
        ShouldBeProblemWithoutLeaks(response, await response.BodyAsync(), 405);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("redis")]
    [InlineData("timeout")]
    public async Task Get_Should_Return503WithRetryAfter_When_ADependencyIsUnavailable(string dependency)
    {
        await using var app = CreateAuthenticatedHost(ArivaEnvironment.K8sPrd);
        using var client = CreateUserClient(app);

        using var response = await client.GetAsync(ThrowingStartupFilter.OutagePath + dependency, TestContext.Current.CancellationToken);
        var body = await response.BodyAsync();

        // ARV-072: the client keeps its data and tries again; the answer says nothing about the dependency.
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(Ariva.Infra.Resilience.DependencyOutage.RetryAfterSeconds));
        ShouldBeProblemWithoutLeaks(response, body, 503);
        body.Should().NotContain(ThrowingStartupFilter.SecretMessage);
    }

    [Fact]
    public async Task Get_Should_Return500_When_ARegexTimesOut()
    {
        await using var app = CreateAuthenticatedHost(ArivaEnvironment.K8sPrd);
        using var client = CreateUserClient(app);

        using var response = await client.GetAsync(ThrowingStartupFilter.OutagePath + "regex", TestContext.Current.CancellationToken);

        // A ReDoS guard refusing an input is not an outage; nothing invites the caller to retry it.
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.RetryAfter.Should().BeNull();
    }

    [Theory]
    [InlineData(ArivaHosts.Main, "/api/v1/admin/audit-entries?targetId=", "SystemAdministrator")]
    [InlineData(ArivaHosts.Main, "/api/v1/sites/ZZ9/validation/campaigns/0199a000-0000-7000-8000-00000000a1a1/tracer-runs?zoneId=", "BorderShiftSupervisor")]
    [InlineData(ArivaHosts.Main, "/api/v1/sites/ZZ9/validation/campaigns/0199a000-0000-7000-8000-00000000a1a1/desk-observations?fromDate=", "BorderShiftSupervisor")]
    [InlineData(ArivaHosts.Main, "/fixture/binding/", "SystemAdministrator")]
    [InlineData(ArivaHosts.Ingest, "/fixture/binding/", "SystemAdministrator")]
    [InlineData(ArivaHosts.Ingest, "/fixture/binding/7?id=", "SystemAdministrator")]
    [InlineData(ArivaHosts.Ingest, "/fixture/binding/7?from=", "SystemAdministrator")]
    [InlineData(ArivaHosts.Integration, "/fixture/binding/", "SystemAdministrator")]
    [InlineData(ArivaHosts.Integration, "/fixture/binding/7?id=", "SystemAdministrator")]
    [InlineData(ArivaHosts.Integration, "/fixture/binding/7?from=", "SystemAdministrator")]
    public async Task Get_Should_Return400WithoutTheValueSent_When_AQueryOrRouteValueDoesNotBind(string host, string path, string role)
    {
        // ARV-104b (CWE-501, CWE-79): the default binding message quoted the value ("The value '<script>...' is not valid");
        // the answer names the field only. Ingest and Integration bind no typed route or query value of their own, so the
        // probe controller proves the shared MVC settings there.
        await using var app = HostWithProbe(host);
        using var client = UserClient(app, role);
        const string payload = "<script>alert(1)</script>' OR '1'='1";

        using var response = await client.GetAsync(path + Uri.EscapeDataString(payload), TestContext.Current.CancellationToken);
        var body = await response.BodyAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        // "The value is not valid for <property>." or, for an action parameter, "The value is not valid."
        body.Should().NotContain("<script>").And.NotContain("alert(1)").And.NotContain("OR '1'").And.NotContain("script").And.Contain("The value is not valid");
    }

    [Theory]
    [InlineData(ArivaHosts.Main, "/api/v1/sites/ZZ9/validation/capture/campaigns/0199a000-0000-7000-8000-00000000a1a1/tracer-runs", "ValidationObserver")]
    [InlineData(ArivaHosts.Main, "/api/v1/admin/alert-rules", "SystemAdministrator")]
    [InlineData(ArivaHosts.Main, "/fixture/binding/body", "SystemAdministrator")]
    [InlineData(ArivaHosts.Ingest, "/fixture/binding/body", "SystemAdministrator")]
    [InlineData(ArivaHosts.Integration, "/fixture/binding/body", "SystemAdministrator")]
    [InlineData(ArivaHosts.Integration, "/api/v1/auth", null)]
    public async Task Post_Should_Return400WithoutTheMembersSent_When_AJsonBodyDoesNotParse(string host, string path, string role)
    {
        // ARV-104b, first security review (CWE-501, CWE-79): System.Text.Json's message and the model state key are the JSON
        // path, which quotes the member names sent ($['<script>alert(1)</script>']). The answer names the body only, with the
        // fixed message. /api/v1/auth is the Integration API's anonymous token exchange.
        await using var app = HostWithProbe(host);
        using var client = role is null ? app.CreateClient() : UserClient(app, role);
        foreach (var json in ScriptMemberBodies)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
            var body = await response.BodyAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            response.Header("Content-Type").Should().StartWith(HttpResponses.ProblemJson);
            body.Should().NotContain("alert(1)").And.NotContainEquivalentOf("script").And.NotContain("LineNumber").And.NotContain("Path:");
            var errors = HttpResponses.ParseProblem(body).GetProperty("errors");
            errors.EnumerateObject().Select(e => e.Name).Should().Equal(ControllerExtensions.BodyErrorKey);
            errors.GetProperty(ControllerExtensions.BodyErrorKey)[0].GetString().Should().Be("The input was not valid.");
        }
    }

    [Fact]
    public async Task Push_Should_Return400WithoutTheMembersSent_When_AnIngestBodyDoesNotParse()
    {
        // Ingest reads its pushes itself (no MVC body binding): a device's malformed push gets one fixed text.
        await using var app = ArivaHosts.Create(ArivaHosts.Ingest, configure: builder => builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, ProbeDeviceGateway>());
            services.Replace(ServiceDescriptor.Singleton<ISensingSink, NoSink>());
        }));
        using var client = app.CreateClient();
        foreach (var json in ScriptMemberBodies)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/zones/Snake%20A/events") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Ariva-Device-Key", ProbeDeviceGateway.Device.Credential);

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var body = await response.BodyAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            body.Should().NotContain("alert(1)").And.NotContainEquivalentOf("script");
        }
    }

    [Theory]
    [InlineData(ArivaHosts.Main)]
    [InlineData(ArivaHosts.Ingest)]
    [InlineData(ArivaHosts.Integration)]
    public async Task Controllers_Should_NeverEchoWhatTheCallerSent_When_AHostIsConfigured(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd);

        app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.AllowInputFormatterExceptionMessages.Should().BeFalse();
        app.Services.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value.InvalidModelStateResponseFactory.Method.Name
            .Should().Be(nameof(ControllerExtensions.ValidationProblemWithoutBodyPaths));
        var messages = app.Services.GetRequiredService<IOptions<MvcOptions>>().Value.ModelBindingMessageProvider;
        messages.AttemptedValueIsInvalidAccessor("<script>", "ZoneId").Should().Be("The value is not valid for ZoneId.");
        messages.NonPropertyAttemptedValueIsInvalidAccessor("<script>").Should().NotContain("<script>");
    }

    [Fact]
    public void WithoutBodyPaths_Should_ReplaceEveryJsonPath_When_TheStateHoldsBodyErrors()
    {
        var state = new ModelStateDictionary();
        state.TryAddModelException("$['<script>alert(1)</script>']", new System.Text.Json.JsonException("'t' is invalid. Path: $['<script>alert(1)</script>']"));
        state.TryAddModelError("$.runs[0].tracerCode", "A message of Ariva's.");
        state.TryAddModelError("request", "The request field is required.");
        state.TryAddModelError(string.Empty, NulRejectingStringConverter.NulMessage);

        var safe = ControllerExtensions.WithoutBodyPaths(state);
        var problem = new ValidationProblemDetails(safe);

        problem.Errors.Keys.Should().BeEquivalentTo(ControllerExtensions.BodyErrorKey, "request", string.Empty);
        problem.Errors[ControllerExtensions.BodyErrorKey].Should().Equal("The input was not valid.", "A message of Ariva's.");
        problem.Errors[string.Empty].Should().Equal(NulRejectingStringConverter.NulMessage);
        string.Concat(problem.Errors.SelectMany(e => e.Value.Prepend(e.Key))).Should().NotContainEquivalentOf("script");
    }

    #endregion

    #region Helpers

    /// <summary>JSON bodies whose member names are markup: the reader fails on the value, inside the member's path.</summary>
    private static readonly string[] ScriptMemberBodies =
    [
        """{"<script>alert(1)</script>": tru}""",
        """{"runs":[{"<script>alert(1)</script>": tru}]}""",
        """{"<SCRIPT>alert(1)</SCRIPT>": "\u0000", "x": tru}"""
    ];

    private static IArivaHost HostWithProbe(string host) =>
        ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            BindingProbeController.Mount(services);
        }));

    private static HttpClient UserClient(IArivaHost app, string role)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "binding-probe");
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RolesHeader, role);
        return client;
    }

    /// <summary>One canonical device of zone "Snake A" at DMO, for the Ingest push.</summary>
    private sealed class ProbeDeviceGateway : ISvcDeviceGateway
    {
        public static readonly DeviceCredentials.Issued Device = DeviceCredentials.New();

        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult(
            prefix == Device.Prefix ? new DeviceCredentialRecord(Guid.NewGuid(), "S-17", "DMO", "Snake A", "Online", Device.Hash, [], null) : null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(Fluentx.Result.Error<DeviceZoneViewModel>("not used"));
    }

    private sealed class NoSink : ISensingSink
    {
        public Task PublishAsync(IReadOnlyList<Ariva.Core.Domain.Contracts.IEvent> events, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static IArivaHost CreateAuthenticatedHost(string environment) =>
        ArivaHosts.Create(ArivaHosts.Main, environment, builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            services.AddSingleton<IStartupFilter, ThrowingStartupFilter>();
        }));

    private static HttpClient CreateUserClient(IArivaHost app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "duty-manager");
        return client;
    }

    private static void ShouldBeProblemWithoutLeaks(HttpResponseMessage response, string body, int status)
    {
        response.Header("Content-Type").Should().StartWith(HttpResponses.ProblemJson);
        HttpResponses.ParseProblem(body).GetProperty("status").GetInt32().Should().Be(status);
        foreach (var marker in HttpResponses.LeakMarkers)
        {
            body.Should().NotContain(marker, "error bodies never reveal stack traces or framework internals");
        }
    }

    #endregion
}
