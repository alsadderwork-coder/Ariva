using System.Net;
using System.Text;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Main.Controllers.AdminArea.Advertising;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-096c (CWE-400, CWE-770, ASVS 5.0 V15.2.2): a form sent to an action that binds no form field or file answers 415 before
/// anything reads it, on every host with controllers. MVC's form value providers would read it otherwise (any action with a
/// parameter), without the request's token, file parts over 64 KB into the pod's <c>/tmp</c>. The tests tell a read form from an
/// unread one: a form of 1,025 fields (past the form reader's 1,024) or a multipart body without its closing boundary answers 400
/// once read, and 415 here. Every action that binds a form is under the <c>upload</c> policy and reads it steadily. The endpoints
/// the login freeze keeps as they are (ARV-089a) are left alone.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class UnboundFormTests
{
    #region Fields

    /// <summary>An action that binds its body as JSON (and a <c>CancellationToken</c>), no form.</summary>
    private const string JsonAction = "/api/v1/admin/alert-rules";

    /// <summary>A frozen sign-in endpoint (ARV-089a): it keeps the form reading it had.</summary>
    private const string FrozenSignIn = "/api/auth/login";

    private const string Boundary = "arivaunbound";

    /// <summary>Sent in a refused form; never in an answer.</summary>
    private const string Canary = "canary-096c";

    /// <summary>A form of 1,025 fields: past the form reader's 1,024, so a read of it answers 400.</summary>
    private static readonly string ManyFields = string.Join('&', Enumerable.Range(0, 1025).Select(i => $"f{i}={Canary}"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Theory data

    /// <summary>The hosts with MVC controllers.</summary>
    public static TheoryData<string> ControllerHosts => [ArivaHosts.Main, ArivaHosts.Ingest, ArivaHosts.Integration];

    /// <summary>Forms the form reader would refuse once it read them (400), with their content type.</summary>
    public static TheoryData<string, string, string> UnreadableForms => new()
    {
        { "1,025 fields", "application/x-www-form-urlencoded", ManyFields },
        { "a multipart body without its closing boundary", $"multipart/form-data; boundary={Boundary}",
            $"--{Boundary}\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\n{Canary}\r\n--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.csv\"\r\n\r\n{Canary}" }
    };

    #endregion

    #region Registration on every host

    [Theory]
    [MemberData(nameof(ControllerHosts))]
    public async Task Filters_Should_BeInstancesWithTheirOwnOrder_When_HostHasControllers(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var filters = app.Services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;

        filters.OfType<UnboundFormFilter>().Should().ContainSingle("one global filter before model binding");
        filters.OfType<NulCharacterFilter>().Select(f => f.Order).Should().BeEquivalentTo([int.MinValue, 0],
            "the NUL check before every other action filter, and the frozen endpoints' at its earlier place until ARV-089a");
        filters.OfType<TypeFilterAttribute>().Select(f => f.ImplementationType).Should().NotContain([typeof(NulCharacterFilter), typeof(UnboundFormFilter), typeof(RepeatedQueryFilter)],
            "a filter added by type is wrapped in one of order 0, which is how the NUL check came to run after the invalid model filter");
        new UnboundFormFilter().Order.Should().BeGreaterThan(new RepeatedQueryFilter().Order).And.BeLessThan(0);
    }

    [Theory]
    [MemberData(nameof(ControllerHosts))]
    public async Task Endpoints_Should_ReadEveryFormSteadilyUnderTheUploadPolicy_When_TheyBindOne(string host)
    {
        await using var app = ArivaHosts.Create(host);
        var metadata = app.Services.GetRequiredService<IModelMetadataProvider>();

        var forms = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action && UnboundFormFilter.BindsForm(action, metadata) &&
                        e.Metadata.GetMetadata<LenientInputMetadata>() is null)
            .ToList();

        if (host != ArivaHosts.Main)
        {
            forms.Should().BeEmpty($"{host} takes no form");
            return;
        }

        forms.Select(Action).Should().BeEquivalentTo(
        [
            "FloorPlans.Upload", "FlightSchedules.Preview", "FlightSchedules.Import", "DeviceImports.Preview", "DeviceImports.Commit",
            "DeskCodeMappingImports.Preview", "DeskCodeMappingImports.Commit", "AdvertisingMedia.Upload"
        ]);
        forms.Should().OnlyContain(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>() != null &&
                                        e.Metadata.GetMetadata<EnableRateLimitingAttribute>().PolicyName == RateLimitingExtensions.UploadPolicy,
            "a form is read only by an upload, one at a time per host");
        forms.Should().OnlyContain(e => e.Metadata.GetMetadata<SteadyUploadAttribute>() != null || e.Metadata.GetMetadata<AdvertisingUploadLimitAttribute>() != null,
            "and read steadily, with the request's token, before model binding");
    }

    #endregion

    #region Answers on a host

    [Theory]
    [MemberData(nameof(UnreadableForms))]
    public async Task Form_Should_Answer415WithoutReadingIt_When_TheActionBindsNone(string form, string contentType, string body)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await client.SendAsync(AsAdministrator(Post(JsonAction, contentType, body)), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType, $"{form} would answer 400 once read");
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.Should().Contain(UnboundFormFilter.Message).And.NotContain(Canary);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Form_Should_Answer401First_When_TheCallerIsAnonymous()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Post(JsonAction, "application/x-www-form-urlencoded", $"name={Canary}"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "default deny answers before any input check");
    }

    [Fact]
    public async Task Form_Should_Answer403First_When_TheRoleLacksThePermission()
    {
        await using var app = Host();
        using var client = app.CreateClient();
        using var request = Post("/api/v1/admin/users", "application/x-www-form-urlencoded", $"userName={Canary}");
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "form-border");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.BorderShiftSupervisor);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "authorization answers before any input check");
    }

    [Fact]
    public async Task Form_Should_Answer415BeforeTheSteadyRead_When_ASteadyActionBindsItsBodyAsJson()
    {
        // The backtest carries SteadyUpload, which would read a form (and answer 400 for one it cannot read): the filter answers
        // first, so the form is never read there either.
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await client.SendAsync(AsAdministrator(Post(JsonAction + "/backtest", "application/x-www-form-urlencoded", ManyFields)), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.Should().Contain(UnboundFormFilter.Message).And.NotContain(SteadyUploadAttribute.Unreadable);
    }

    [Fact]
    public async Task Json_Should_StillReachTheAction_When_TheActionBindsItsBody()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await client.SendAsync(AsAdministrator(Post(JsonAction, "application/json", """{"siteCode":"DMO"}""")), Ct);

        response.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType, "only a form is refused here");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(UnboundFormFilter.Message);
    }

    [Fact]
    public async Task Form_Should_BeLeftAsItWas_When_TheEndpointIsFrozenUntilArv089a()
    {
        // The login freeze: the sign-in endpoints read a form as they did before (MVC's form value providers, whose reader refuses
        // 1,025 fields with 400). StrictInputTests pins the token exchange and the frozen NUL check the same way.
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Post(FrozenSignIn, "application/x-www-form-urlencoded", ManyFields), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "read and refused by the form reader, as before");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(UnboundFormFilter.Message);
    }

    #endregion

    #region What binds a form

    [Theory]
    [InlineData("no parameter", false)]
    [InlineData("a cancellation token", false)]
    [InlineData("a JSON body", false)]
    [InlineData("a query value", false)]
    [InlineData("a route value", false)]
    [InlineData("a query model", false)]
    [InlineData("a file", true)]
    [InlineData("a form field", true)]
    [InlineData("model binding's own source", true)]
    [InlineData("no source", true)]
    [InlineData("a custom binder", true)]
    [InlineData("a query model with a form property", true)]
    [InlineData("a bound controller property from the form", true)]
    public async Task BindsForm_Should_AnswerWhetherAFormMayBeRead_When_TheActionBindsThis(string binds, bool expected)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        var metadata = app.Services.GetRequiredService<IModelMetadataProvider>();
        var action = new ControllerActionDescriptor { Parameters = [], BoundProperties = [] };
        switch (binds)
        {
            case "no parameter":
                break;
            case "a cancellation token":
                action.Parameters.Add(Parameter<CancellationToken>(BindingSource.Special));
                break;
            case "a JSON body":
                action.Parameters.Add(Parameter<Ariva.Core.Domain.InputModels.ImportSiteConfigurationRequest>(BindingSource.Body));
                break;
            case "a query value":
                action.Parameters.Add(Parameter<string>(BindingSource.Query));
                break;
            case "a route value":
                action.Parameters.Add(Parameter<string>(BindingSource.Path));
                break;
            case "a query model":
                action.Parameters.Add(Parameter<QueryModel>(BindingSource.Query));
                break;
            case "a file":
                action.Parameters.Add(Parameter<IFormFile>(BindingSource.FormFile));
                break;
            case "a form field":
                action.Parameters.Add(Parameter<string>(BindingSource.Form));
                break;
            case "model binding's own source":
                action.Parameters.Add(Parameter<string>(BindingSource.ModelBinding));
                break;
            case "no source":
                action.Parameters.Add(new ParameterDescriptor { Name = "value", ParameterType = typeof(string) });
                break;
            case "a custom binder":
                action.Parameters.Add(Parameter<string>(BindingSource.Custom));
                break;
            case "a query model with a form property":
                action.Parameters.Add(Parameter<QueryModelWithFormProperty>(BindingSource.Query));
                break;
            case "a bound controller property from the form":
                action.BoundProperties.Add(Parameter<string>(BindingSource.Form));
                break;
        }

        UnboundFormFilter.BindsForm(action, metadata).Should().Be(expected, binds);
    }

    [Fact]
    public void Filter_Should_LeaveARequestWithoutAFormAlone_When_TheActionBindsNone()
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        var context = new ResourceExecutingContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new ActionDescriptor()), [], []);

        new UnboundFormFilter().OnResourceExecuting(context);

        context.Result.Should().BeNull();
    }

    #endregion

    #region Helpers

    private static IArivaHost Host() =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            FakeAlertRules.Register(services);
        }));

    private static HttpRequestMessage Post(string path, string contentType, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
    }

    private static HttpRequestMessage AsAdministrator(HttpRequestMessage request)
    {
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "form-admin");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);
        return request;
    }

    private static ParameterDescriptor Parameter<T>(BindingSource source) =>
        new() { Name = "value", ParameterType = typeof(T), BindingInfo = new BindingInfo { BindingSource = source } };

    private static string Action(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action ? $"{action.ControllerName}.{action.ActionName}" : endpoint.DisplayName ?? string.Empty;

    /// <summary>A query model whose properties all come from the query.</summary>
    public sealed class QueryModel
    {
        public string Text { get; set; }

        public int? Page { get; set; }
    }

    /// <summary>A query model with a property from the form.</summary>
    public sealed class QueryModelWithFormProperty
    {
        public string Text { get; set; }

        [FromForm]
        public string Note { get; set; }
    }

    #endregion
}
