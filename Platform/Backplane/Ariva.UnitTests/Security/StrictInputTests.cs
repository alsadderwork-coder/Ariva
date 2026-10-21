using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-089 (ASVS 5.0 V2.2.1, V15.3.5, V15.3.7; CWE-20, CWE-501): request input is read strictly on every host. Controller and
/// minimal API JSON refuse a member the request model does not have, a member sent twice and a number sent as a string;
/// a query parameter sent twice is refused unless the action binds a collection under that name. Every refusal is the
/// 400 that repeats nothing the caller sent. The login freeze keeps the two authentication controllers on the lenient
/// parsing until ARV-089a, and nothing else.
/// </summary>
[Collection(HostCollection.Name)]
public sealed partial class StrictInputTests
{
    #region Fields

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid LevelId = Guid.Parse("0199a000-0000-7000-8000-00000000c0c0");

    private static string CalibrationPath => $"/api/v1/admin/levels/{LevelId}/floor-plan/calibration";

    #endregion

    #region Theory data

    /// <summary>Calibration bodies that are well-formed JSON but not strict JSON, each with text that must not come back.</summary>
    public static TheoryData<string, string, string> NotStrictCalibrations => new()
    {
        { "an unknown member", """{"metresPerPixel":0.0125,"originX":5,"originY":7,"zIndexProbe":1}""", "zIndexProbe" },
        { "an unknown member named with markup", """{"metresPerPixel":0.0125,"originX":5,"originY":7,"<script>alert(1)</script>":true}""", "script" },
        { "a member twice", """{"metresPerPixel":0.0125,"originX":5,"originX":6.25,"originY":7}""", "6.25" },
        { "a member twice in another case", """{"metresPerPixel":0.0125,"originX":5,"OriginX":6.25,"originY":7}""", "OriginX" },
        { "a number as a string", """{"metresPerPixel":"0.0125","originX":5,"originY":7}""", "0.0125" },
        { "a number as a string in another member", """{"metresPerPixel":0.0125,"originX":"5.75","originY":7}""", "5.75" },
        { "an unknown nested member", """{"metresPerPixel":0.0125,"originX":5,"originY":7,"extra":{"metresPerPixel":1}}""", "extra" }
    };

    /// <summary>The probe's bodies that are not strict JSON, with text that must not come back.</summary>
    private static readonly (string Why, string Json, string Marker)[] NotStrictProbeBodies =
    [
        ("an unknown member", """{"name":"ok","<script>alert(1)</script>":1}""", "script"),
        ("a member twice", """{"name":"ok","name":"evil-twin"}""", "evil-twin"),
        ("a number as a string", """{"name":"ok","count":"7319"}""", "7319")
    ];

    /// <summary>The hosts with MVC controllers, and whether the login freeze keeps one of their controllers lenient.</summary>
    public static TheoryData<string, Type> ControllerHosts => new()
    {
        { ArivaHosts.Main, typeof(Ariva.Api.Main.Controllers.Auth.AuthController) },
        { ArivaHosts.Ingest, null },
        { ArivaHosts.Integration, typeof(Ariva.Api.Integration.Controllers.AuthController) }
    };

    #endregion

    #region Options on every host

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task GetJsonOptions_Should_ReadStrictly_When_HostIsBuilt(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var minimalApis = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var controllers = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;

        foreach (var options in new[] { minimalApis, controllers })
        {
            options.UnmappedMemberHandling.Should().Be(JsonUnmappedMemberHandling.Disallow);
            options.AllowDuplicateProperties.Should().BeFalse();
            options.NumberHandling.Should().Be(JsonNumberHandling.Strict, "ASP.NET Core's web defaults read numbers from strings");
            options.MaxDepth.Should().Be(32);
            StrictJson.IsStrict(options).Should().BeTrue();
        }
    }

    [Theory]
    [MemberData(nameof(ControllerHosts))]
    public async Task InputFormatters_Should_ReadStrictlyExceptForTheFrozenEndpoints_When_HostHasControllers(string host, Type frozen)
    {
        await using var app = ArivaHosts.Create(host);

        var mvc = app.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var json = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value;
        var formatters = mvc.InputFormatters.OfType<SystemTextJsonInputFormatter>().ToList();

        var strict = formatters.Single(formatter => formatter.GetType() == typeof(SystemTextJsonInputFormatter));
        strict.SerializerOptions.Should().BeSameAs(json.JsonSerializerOptions);
        StrictJson.IsStrict(strict.SerializerOptions).Should().BeTrue();
        json.AllowInputFormatterExceptionMessages.Should().BeFalse("a 400 never quotes the reader's message, which holds the path");
        if (frozen is null)
        {
            formatters.Should().ContainSingle("no endpoint of this host keeps the lenient parsing");
            return;
        }

        // TEMPORARY until ARV-089a: the formatter with the pre-ARV-089 settings reads first, for the frozen endpoints only.
        var relaxed = formatters.Single(formatter => formatter != strict);
        mvc.InputFormatters[0].Should().BeSameAs(relaxed);
        relaxed.SerializerOptions.Should().NotBeSameAs(strict.SerializerOptions);
        relaxed.SerializerOptions.UnmappedMemberHandling.Should().Be(JsonUnmappedMemberHandling.Skip);
        relaxed.SerializerOptions.AllowDuplicateProperties.Should().BeTrue();
        relaxed.SerializerOptions.NumberHandling.Should().Be(JsonNumberHandling.AllowReadingFromString);
        relaxed.SerializerOptions.MaxDepth.Should().Be(32, "everything else is the controllers' configured options");
        relaxed.SerializerOptions.Converters.OfType<NulRejectingStringConverter>().Should().ContainSingle();
        relaxed.GetSupportedContentTypes("application/json", typeof(LoginRequest)).Should().BeNull("the API description lists the strict formatter only");
    }

    [Theory]
    [MemberData(nameof(ControllerHosts))]
    public async Task Endpoints_Should_KeepTheLenientParsingOnlyOnTheAuthControllers_When_HostIsBuilt(string host, Type frozen)
    {
        await using var app = ArivaHosts.Create(host);

        var lenient = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<LenientInputMetadata>() is not null)
            .Select(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>())
            .ToList();

        if (frozen is null)
        {
            lenient.Should().BeEmpty();
            return;
        }

        lenient.Should().OnlyContain(action => action != null && action.ControllerTypeInfo.AsType() == frozen, "the exemption is the login freeze's, nothing else");
        var actions = frozen.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(method => method.GetCustomAttributes<HttpMethodAttribute>().Any());
        lenient.Select(action => action.ActionName).Distinct().Should().HaveCount(actions, "every action of the controller keeps it until ARV-089a");
    }

    [Theory]
    [MemberData(nameof(ControllerHosts))]
    public void FrozenBodies_Should_HoldOnlyText_When_TheyKeepTheLenientParsing(string host, Type frozen)
    {
        // Lenient number handling has nothing to act on while every member of a frozen body is a string (V15.3.5); a number
        // member added before ARV-089a would be read from a string again, so it fails here first.
        if (frozen is null) return;

        var bodies = frozen.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.GetCustomAttribute<FromBodyAttribute>() is not null)
            .Select(parameter => parameter.ParameterType)
            .Distinct()
            .ToList();

        bodies.Should().NotBeEmpty($"{host}'s frozen controller takes JSON bodies");
        bodies.SelectMany(body => body.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Should().OnlyContain(property => property.PropertyType == typeof(string), $"{host}'s frozen bodies hold text only until ARV-089a");
    }

    #endregion

    #region JSON bodies on Ariva.Api.Main

    [Theory]
    [MemberData(nameof(NotStrictCalibrations))]
    public async Task Calibrate_Should_Answer400WithoutEcho_When_TheBodyIsNotStrictJson(string why, string json, string marker)
    {
        var floorPlans = new RecordingFloorPlans();
        await using var app = MainWith(floorPlans);
        using var client = UserClient(app, Ariva.Core.RoleCodes.SystemAdministrator);

        using var response = await client.PutAsync(CalibrationPath, Json(json), Ct);
        var body = WithoutTraceId(await response.BodyAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, why);
        response.Header("Content-Type").Should().StartWith(HttpResponses.ProblemJson);
        var errors = HttpResponses.ParseProblem(body).GetProperty("errors");
        errors.EnumerateObject().Select(e => e.Name).Should().Equal([ControllerExtensions.BodyErrorKey], why);
        errors.GetProperty(ControllerExtensions.BodyErrorKey)[0].GetString().Should().Be("The input was not valid.");
        body.Should().NotContain(marker, why).And.NotContain("Path:").And.NotContain("LineNumber");
        floorPlans.Calibrations.Should().BeEmpty("nothing that is not strict JSON reaches a service");
    }

    [Fact]
    public async Task Calibrate_Should_TakeTheBodyAndAnswerNumbersAsBefore_When_TheBodyIsStrictJson()
    {
        var floorPlans = new RecordingFloorPlans();
        await using var app = MainWith(floorPlans);
        using var client = UserClient(app, Ariva.Core.RoleCodes.SystemAdministrator);

        using var response = await client.PutAsync(CalibrationPath, Json("""{"metresPerPixel":0.0125,"originX":5,"originY":-7.5}"""), Ct);
        // Member names still match ignoring case (the web defaults): only unknown, repeated and string-typed numbers changed.
        using var pascal = await client.PutAsync(CalibrationPath, Json("""{"MetresPerPixel":0.02,"OriginX":1,"OriginY":2}"""), Ct);
        var answer = HttpResponses.ParseProblem(await response.BodyAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        pascal.StatusCode.Should().Be(HttpStatusCode.OK);
        floorPlans.Calibrations.Should().Equal(new CalibrateFloorPlanRequest(0.0125, 5, -7.5), new CalibrateFloorPlanRequest(0.02, 1, 2));
        answer.GetProperty("metresPerPixel").ValueKind.Should().Be(JsonValueKind.Number, "responses write numbers as numbers, as before");
        answer.GetProperty("metresPerPixel").GetDouble().Should().Be(0.0125);
        answer.GetProperty("widthPixels").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Calibrate_Should_AskForAuthenticationAndPermissionFirst_When_TheBodyIsNotStrictJson()
    {
        await using var app = MainWith(new RecordingFloorPlans());
        using var anonymous = app.CreateClient();
        using var handler = UserClient(app, Ariva.Core.RoleCodes.HandlerStationManager);
        const string json = """{"metresPerPixel":"0.0125","originX":5,"originX":5,"originY":7,"extra":1}""";

        using var unauthenticated = await anonymous.PutAsync(CalibrationPath, Json(json), Ct);
        using var forbidden = await handler.PutAsync(CalibrationPath, Json(json), Ct);

        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "default deny answers before any input check");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    #endregion

    #region Repeated query parameters

    [Theory]
    [InlineData("?text=<script>alert(1)</script>&text=x")]
    [InlineData("?text=first-probe&TEXT=second-probe")]
    [InlineData("?text=a&role=SystemAdministrator&role=BorderShiftSupervisor")]
    [InlineData("?pageSize=1&pageSize=500")]
    [InlineData("?unknown=one-probe&unknown=two-probe")]
    [InlineData("?text=&text=")]
    public async Task Search_Should_Answer400WithoutEcho_When_AQueryParameterIsRepeated(string query)
    {
        await using var app = MainWithAdministration();
        using var client = UserClient(app, Ariva.Core.RoleCodes.SystemAdministrator);

        using var response = await client.GetAsync("/api/v1/admin/users" + query, Ct);
        var body = await response.BodyAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Header("Content-Type").Should().StartWith(HttpResponses.ProblemJson);
        HttpResponses.ParseProblem(body).GetProperty("detail").GetString().Should().Be(RepeatedQueryFilter.Message);
        body.Should().NotContainEquivalentOf("script").And.NotContain("probe").And.NotContain("text").And.NotContain("pageSize").And.NotContain("role");
    }

    [Fact]
    public async Task Search_Should_Answer200_When_EveryQueryParameterIsSentOnce()
    {
        await using var app = MainWithAdministration();
        using var client = UserClient(app, Ariva.Core.RoleCodes.SystemAdministrator);

        using var response = await client.GetAsync("/api/v1/admin/users?text=a&role=SystemAdministrator&pageSize=10", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_Should_AskForAuthenticationAndPermissionFirst_When_AQueryParameterIsRepeated()
    {
        await using var app = MainWithAdministration();
        using var anonymous = app.CreateClient();
        using var border = UserClient(app, Ariva.Core.RoleCodes.BorderShiftSupervisor);

        using var unauthenticated = await anonymous.GetAsync("/api/v1/admin/users?text=a&text=b", Ct);
        using var forbidden = await border.GetAsync("/api/v1/admin/users?text=a&text=b", Ct);

        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Device_Should_Answer400_When_AQueryParameterIsRepeated()
    {
        // Ariva.Api.Ingest's device endpoints bind nothing from the query, so every repeated parameter is refused.
        await using var app = ArivaHosts.Create(ArivaHosts.Ingest, configure: builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, OneDeviceGateway>())));
        using var client = app.CreateClient();

        using var repeated = await client.SendAsync(AsDevice("/api/v1/ingest/device?zone=Snake%20A&zone=Snake%20B"), Ct);
        using var once = await client.SendAsync(AsDevice("/api/v1/ingest/device?zone=Snake%20A"), Ct);
        using var anonymous = await client.GetAsync("/api/v1/ingest/device?zone=a&zone=b", Ct);
        var body = await repeated.BodyAsync();

        repeated.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HttpResponses.ParseProblem(body).GetProperty("detail").GetString().Should().Be(RepeatedQueryFilter.Message);
        body.Should().NotContain("Snake").And.NotContain("zone");
        once.StatusCode.Should().Be(HttpStatusCode.OK);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the device credential is checked first");
    }

    #endregion

    #region The shared settings on every host with controllers

    [Theory]
    [InlineData(ArivaHosts.Main)]
    [InlineData(ArivaHosts.Ingest)]
    [InlineData(ArivaHosts.Integration)]
    public async Task ProbeBody_Should_Answer400WithoutEcho_When_TheBodyIsNotStrictJson(string host)
    {
        await using var app = HostWithProbe(host);
        using var client = UserClient(app, Ariva.Core.RoleCodes.SystemAdministrator);
        foreach (var (why, json, marker) in NotStrictProbeBodies)
        {
            using var response = await client.PostAsync("/fixture/binding/body", Json(json), Ct);
            var body = WithoutTraceId(await response.BodyAsync());

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{host}: {why}");
            HttpResponses.ParseProblem(body).GetProperty("errors").EnumerateObject().Select(e => e.Name).Should().Equal(ControllerExtensions.BodyErrorKey);
            body.Should().NotContainEquivalentOf(marker, $"{host}: {why}");
        }

        using var strict = await client.PostAsync("/fixture/binding/body", Json("""{"name":"ok","count":3}"""), Ct);
        strict.StatusCode.Should().Be(HttpStatusCode.OK);
        (await strict.BodyAsync()).Should().Be("""{"id":null,"name":"ok","count":3}""", "the response is written as before");
    }

    [Theory]
    [InlineData(ArivaHosts.Main)]
    [InlineData(ArivaHosts.Ingest)]
    [InlineData(ArivaHosts.Integration)]
    public async Task ProbeQuery_Should_Answer400WithoutEcho_When_AQueryParameterIsRepeated(string host)
    {
        await using var app = HostWithProbe(host);
        using var client = UserClient(app, Ariva.Core.RoleCodes.SystemAdministrator);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        foreach (var query in new[] { $"?id={first}&id={second}", $"?id={first}&ID={second}", "?from=2026-10-01&From=2026-10-02", "?x=<script>&x=1" })
        {
            using var response = await client.GetAsync("/fixture/binding/7" + query, Ct);
            var body = await response.BodyAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{host} {query}");
            HttpResponses.ParseProblem(body).GetProperty("detail").GetString().Should().Be(RepeatedQueryFilter.Message);
            body.Should().NotContain(first.ToString()).And.NotContain(second.ToString()).And.NotContain("2026-10").And.NotContainEquivalentOf("script");
        }

        using var once = await client.GetAsync($"/fixture/binding/7?id={first}&from=2026-10-01", Ct);
        using var anonymous = app.CreateClient();
        using var unauthenticated = await anonymous.GetAsync($"/fixture/binding/7?id={first}&id={second}", Ct);
        once.StatusCode.Should().Be(HttpStatusCode.OK);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Login freeze (TEMPORARY until ARV-089a)

    [Fact]
    public async Task Login_Should_KeepTheLenientParsing_When_TheBodyHasAnExtraMemberOrADuplicateOrTheQueryARepeat()
    {
        // The owner's login freeze: sign-in behaves as before ARV-089. Each body reaches the sign-in service (which refuses the
        // credentials, 401), so none of them is refused for its parsing; a body that is not JSON is still the fixed 400.
        var authenticator = new FakeAuthenticator();
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<Ariva.Core.Services.Security.ISvcAuthenticator>(authenticator))));
        using var client = app.CreateClient();

        using var extra = await client.PostAsync("/api/auth/login", Json("""{"userName":"freeze","password":"not-the-password","rememberMe":true}"""), Ct);
        using var twice = await client.PostAsync("/api/auth/login", Json("""{"userName":"freeze","userName":"freeze2","password":"not-the-password"}"""), Ct);
        using var repeated = await client.PostAsync("/api/auth/login?client=web&client=web", Json("""{"userName":"freeze","password":"not-the-password"}"""), Ct);
        using var broken = await client.PostAsync("/api/auth/login", Json("""{"userName": tru}"""), Ct);

        extra.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an extra member is skipped, as before");
        twice.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a duplicate is read as before");
        repeated.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a repeated query parameter is not refused here");
        authenticator.Contexts.Should().HaveCount(3, "each of the three reached the sign-in service");
        broken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HttpResponses.ParseProblem(await broken.BodyAsync()).GetProperty("errors").GetProperty(ControllerExtensions.BodyErrorKey)[0].GetString()
            .Should().Be("The input was not valid.");
    }

    [Fact]
    public async Task Exchange_Should_KeepTheLenientParsing_When_TheBodyHasAnExtraMemberOrADuplicateOrTheQueryARepeat()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Integration, configure: builder => builder.ConfigureTestServices(FakeIntegrationClients.Register));
        using var client = app.CreateClient();
        const string exchange = """{"clientId":"ic_aaaaaaaaaaaaaaaaaaaaaaaaaa","clientSecret":"not-the-secret","totpCode":"123456"}""";

        using var extra = await client.PostAsync("/api/v1/auth", Json(exchange[..^1] + ""","grant_type":"client_credentials"}"""), Ct);
        using var twice = await client.PostAsync("/api/v1/auth", Json(exchange[..^1] + ""","totpCode":"654321"}"""), Ct);
        using var repeated = await client.PostAsync("/api/v1/auth?scope=a&scope=b", Json(exchange), Ct);

        foreach (var response in new[] { extra, twice, repeated })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the token exchange reads its body as before");
            (await response.BodyAsync()).Should().Contain("invalid_client");
        }
    }

    [Fact]
    public async Task Login_Should_KeepItsNulCheckAndFormReading_When_TheLoginFreezeHolds()
    {
        // ARV-096c: the frozen endpoints keep the NUL check where it ran before (after the invalid model filter) and read a form as
        // they did (the form reader's 400 for a form of 1,025 fields), so their answers do not change until ARV-089a.
        var authenticator = new FakeAuthenticator();
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<Ariva.Core.Services.Security.ISvcAuthenticator>(authenticator))));
        using var client = app.CreateClient();

        using var nul = await client.PostAsync("/api/auth/login?x=%00", Json("""{"userName":"freeze","password":"not-the-password"}"""), Ct);
        using var nulAndBroken = await client.PostAsync("/api/auth/login?x=%00", Json("""{"userName": tru}"""), Ct);
        using var form = await client.PostAsync("/api/auth/login", ManyFields(), Ct);

        nul.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HttpResponses.ParseProblem(await nul.BodyAsync()).GetProperty("detail").GetString().Should().Be("The request contains a NUL character.");
        nulAndBroken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HttpResponses.ParseProblem(await nulAndBroken.BodyAsync()).GetProperty("errors").GetProperty(ControllerExtensions.BodyErrorKey)[0].GetString()
            .Should().Be("The input was not valid.", "the invalid model filter still answers before the NUL check here, as before ARV-096c");
        form.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the form is read and refused by the form reader, as before ARV-096c");
        (await form.BodyAsync()).Should().NotContain(UnboundFormFilter.Message);
        authenticator.Contexts.Should().BeEmpty("none of them reaches the sign-in service");
    }

    [Fact]
    public async Task Exchange_Should_KeepItsNulCheckAndFormReading_When_TheLoginFreezeHolds()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Integration, configure: builder => builder.ConfigureTestServices(FakeIntegrationClients.Register));
        using var client = app.CreateClient();

        using var nul = await client.PostAsync("/api/v1/auth?x=%00",
            Json("""{"clientId":"ic_aaaaaaaaaaaaaaaaaaaaaaaaaa","clientSecret":"not-the-secret","totpCode":"123456"}"""), Ct);
        using var form = await client.PostAsync("/api/v1/auth", ManyFields(), Ct);

        nul.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HttpResponses.ParseProblem(await nul.BodyAsync()).GetProperty("detail").GetString().Should().Be("The request contains a NUL character.");
        form.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the form is read and refused by the form reader, as before ARV-096c");
        (await form.BodyAsync()).Should().NotContain(UnboundFormFilter.Message);
    }

    /// <summary>A form of 1,025 fields: past the form reader's 1,024.</summary>
    private static ByteArrayContent ManyFields()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(string.Join('&', Enumerable.Range(0, 1025).Select(i => $"f{i}=v"))));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        return content;
    }

    #endregion

    #region The filter

    [Fact]
    public void OnResourceExecuting_Should_AllowRepeats_When_TheActionBindsACollectionFromTheQueryByThatName()
    {
        var action = Action(Parameter("ids", typeof(Guid[]), BindingSource.Query));

        Refused(action, "?ids=a&ids=b").Should().BeFalse();
        Refused(action, "?IDS=a&ids=b").Should().BeFalse("names compare ignoring case");
        Refused(action, "?other=a&other=b").Should().BeTrue();
        Refused(action, "?ids=a&other=b").Should().BeFalse("no key is repeated");
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(Guid?))]
    public void OnResourceExecuting_Should_Refuse_When_TheParameterBindsOneValue(Type type)
    {
        var action = Action(Parameter("zone", type, BindingSource.Query));

        Refused(action, "?zone=a&zone=b").Should().BeTrue();
        Refused(action, "?zone=a&ZONE=b").Should().BeTrue();
        Refused(action, "?zone=a").Should().BeFalse();
    }

    [Fact]
    public void OnResourceExecuting_Should_UseTheBinderName_When_TheParameterHasOne()
    {
        var action = Action(Parameter("ids", typeof(List<string>), BindingSource.Query, binderName: "id"));

        Refused(action, "?id=a&id=b").Should().BeFalse();
        Refused(action, "?ids=a&ids=b").Should().BeTrue("the query name is the binder name");
    }

    [Fact]
    public void OnResourceExecuting_Should_Refuse_When_TheCollectionComesFromTheBody()
    {
        var action = Action(Parameter("ids", typeof(string[]), BindingSource.Body), Parameter("text", typeof(string), BindingSource.Query));

        Refused(action, "?ids=a&ids=b").Should().BeTrue();
        Refused(action, "?text=a&text=b").Should().BeTrue();
    }

    [Theory]
    [InlineData("?zones=a&zones=b", false)]
    [InlineData("?criteria.zones=a&criteria.zones=b", false)]
    [InlineData("?Criteria.Zones=a&criteria.zones=b", false)]
    [InlineData("?lane=a&lane=b", false)]
    [InlineData("?criteria.lane=a&criteria.lane=b", false)]
    [InlineData("?inner.levels=1&inner.levels=2", false)]
    [InlineData("?criteria.inner.levels=1&criteria.inner.levels=2", false)]
    [InlineData("?text=a&text=b", true)]
    [InlineData("?lanes=a&lanes=b", true)]
    [InlineData("?hidden=a&hidden=b", true)]
    [InlineData("?criteria.text=a&criteria.text=b", true)]
    [InlineData("?inner=a&inner=b", true)]
    public void OnResourceExecuting_Should_FollowTheQueryModel_When_TheParameterIsComplex(string query, bool refused)
    {
        var action = Action(Parameter("criteria", typeof(ProbeCriteria), BindingSource.Query));

        Refused(action, query).Should().Be(refused, query);
    }

    [Theory]
    [InlineData("?id=a&%69d=b", true)]
    [InlineData("?id=a&%49D=b", true)]
    [InlineData("?id=a&id", true)]
    [InlineData("?id=a&id=", true)]
    [InlineData("?=1&=2", true)]
    [InlineData("?ids[0]=a&ids[0]=b", true)]
    [InlineData("?ids=a&ids=b", false)]
    [InlineData("?ids=a&ids[0]=b", false)]
    [InlineData("?id=a&ids[0]=b&ids[1]=c", false)]
    public void OnResourceExecuting_Should_JudgeTheDecodedKeys_When_AKeyIsEncodedIndexedOrEmpty(string query, bool refused)
    {
        // The filter reads Request.Query, the collection the value providers bind from: keys arrive decoded and compare
        // ignoring case, so an encoded or empty duplicate is still a duplicate, and an indexed key repeated is refused.
        var action = Action(Parameter("id", typeof(string), BindingSource.Query), Parameter("ids", typeof(Guid[]), BindingSource.Query));

        Refused(action, query).Should().Be(refused, query);
    }

    [Fact]
    public void OnResourceExecuting_Should_LeaveTheRequestAlone_When_TheEndpointKeepsTheLenientParsing()
    {
        var action = Action(Parameter("zone", typeof(string), BindingSource.Query));
        action.EndpointMetadata.Add(LenientInputMetadata.Instance);

        Refused(action, "?zone=a&zone=b").Should().BeFalse("TEMPORARY until ARV-089a: the login freeze");
    }

    [Fact]
    public void OnResourceExecuting_Should_AnswerTheFixedProblem_When_AParameterIsRepeated()
    {
        var context = Context(Action(), "?%3Cscript%3E=1&%3Cscript%3E=2");

        new RepeatedQueryFilter().OnResourceExecuting(context);

        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.ContentTypes.Should().Contain("application/problem+json");
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Detail.Should().Be(RepeatedQueryFilter.Message);
        JsonSerializer.Serialize(problem).Should().NotContainEquivalentOf("script");
    }

    [Fact]
    public void QueryCollectionNames_Should_UseParameterTypesOnly_When_NoMetadataProviderIsGiven()
    {
        var action = Action(Parameter("ids", typeof(IEnumerable<int>), null), Parameter("criteria", typeof(ProbeCriteria), BindingSource.Query));

        RepeatedQueryFilter.QueryCollectionNames(action, null).Should().BeEquivalentTo(["ids"]);
    }

    #endregion

    #region StrictJson

    [Fact]
    public void Apply_Should_MakeOptionsStrict_And_Relax_Should_TakeThemBackToTheWebDefaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32 };

        StrictJson.IsStrict(options).Should().BeFalse();
        StrictJson.Apply(options);
        StrictJson.IsStrict(options).Should().BeTrue();
        StrictJson.Relax(options);

        StrictJson.IsStrict(options).Should().BeFalse();
        options.UnmappedMemberHandling.Should().Be(JsonUnmappedMemberHandling.Skip);
        options.AllowDuplicateProperties.Should().BeTrue();
        options.NumberHandling.Should().Be(JsonNumberHandling.AllowReadingFromString);
        options.MaxDepth.Should().Be(32, "only the three strict settings are taken back");
    }

    [Theory]
    [InlineData("""{"name":"a","extra":1}""")]
    [InlineData("""{"name":"a","name":"b"}""")]
    [InlineData("""{"name":"a","Name":"b"}""")]
    [InlineData("""{"name":"a","count":"5"}""")]
    [InlineData("""{"name":"a","sizes":[1,"2"]}""")]
    [InlineData("""{"name":"a","labels":{"en":"x","en":"y"}}""")]
    public void Deserialize_Should_Refuse_When_TheJsonIsNotStrict(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        StrictJson.Apply(options);
        var lenient = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var strict = () => JsonSerializer.Deserialize<Sample>(json, options);
        var before = () => JsonSerializer.Deserialize<Sample>(json, lenient);

        strict.Should().Throw<JsonException>();
        before.Should().NotThrow("ASP.NET Core's web defaults took it");
    }

    [Fact]
    public void Serialize_Should_WriteTheSameJson_When_TheOptionsAreStrict()
    {
        // Responses: NumberHandling.Strict writes numbers as numbers, as AllowReadingFromString did, and the other two settings
        // only read, so a host's answers are byte for byte what they were.
        var before = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new NulRejectingStringConverter() } };
        var after = new JsonSerializerOptions(before);
        StrictJson.Apply(after);
        var sample = new Sample("Arrivals القادمون", 7, [1, 2], new Dictionary<string, string> { ["en"] = "x" })
        {
            Ratio = 0.125,
            Amount = 12.50m,
            Big = long.MaxValue,
            Missing = null,
            Keyed = new Dictionary<int, double> { [3] = 1.5 },
            Day = DayOfWeek.Friday
        };

        JsonSerializer.Serialize(sample, after).Should().Be(JsonSerializer.Serialize(sample, before));
    }

    #endregion

    #region Helpers

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>The traceId is random hex and may hold any run of digits: blanked before an echo check.</summary>
    private static string WithoutTraceId(string body) => TraceId().Replace(body, "\"traceId\":\"\"");

    [GeneratedRegex("\"traceId\":\"[^\"]*\"", RegexOptions.None, 1000)]
    private static partial Regex TraceId();

    private static HttpClient UserClient(IArivaHost app, string role)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "strict-input");
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RolesHeader, role);
        return client;
    }

    private static IArivaHost MainWith(RecordingFloorPlans floorPlans) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISvcFloorPlans>(_ => floorPlans));
        }));

    private static IArivaHost MainWithAdministration() =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
        }));

    private static IArivaHost HostWithProbe(string host) =>
        ArivaHosts.Create(host, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            BindingProbeController.Mount(services);
        }));

    private static HttpRequestMessage AsDevice(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(DeviceAuthentication.KeyHeader, OneDeviceGateway.Device.Credential);
        return request;
    }

    private static ControllerActionDescriptor Action(params ParameterDescriptor[] parameters) =>
        new() { Parameters = [.. parameters], EndpointMetadata = [], ActionName = "Probe", ControllerName = "Probe" };

    private static ParameterDescriptor Parameter(string name, Type type, BindingSource source, string binderName = null) =>
        new() { Name = name, ParameterType = type, BindingInfo = source is null && binderName is null ? null : new BindingInfo { BindingSource = source, BinderModelName = binderName } };

    private static readonly Lazy<IServiceProvider> MvcServices = new(() =>
        new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider());

    private static ResourceExecutingContext Context(ActionDescriptor action, string query)
    {
        var http = new DefaultHttpContext { RequestServices = MvcServices.Value };
        http.Request.QueryString = new QueryString(query);
        return new ResourceExecutingContext(new ActionContext(http, new RouteData(), action), [], []);
    }

    private static bool Refused(ActionDescriptor action, string query)
    {
        var context = Context(action, query);
        new RepeatedQueryFilter().OnResourceExecuting(context);
        return context.Result is not null;
    }

    #endregion

    #region Fixtures

    /// <summary>A query model with collections at several places, for the filter's model walk.</summary>
    public sealed class ProbeCriteria
    {
        public IReadOnlyList<string> Zones { get; set; }

        public string Text { get; set; }

        [FromQuery(Name = "lane")]
        public string[] Lanes { get; set; }

        [BindNever]
        public List<string> Hidden { get; set; }

        public ProbeInner Inner { get; set; }
    }

    /// <summary>A nested part of <see cref="ProbeCriteria"/>.</summary>
    public sealed class ProbeInner
    {
        public List<int> Levels { get; set; }
    }

    /// <summary>A body with numbers, a list and a dictionary, read and written with and without the strict settings.</summary>
    public sealed record Sample(string Name, int Count, IReadOnlyList<int> Sizes, IReadOnlyDictionary<string, string> Labels)
    {
        public double Ratio { get; init; }

        public decimal Amount { get; init; }

        public long Big { get; init; }

        public int? Missing { get; init; }

        public IReadOnlyDictionary<int, double> Keyed { get; init; }

        public DayOfWeek Day { get; init; }
    }

    /// <summary>Floor plans whose calibration answers with what it was given, and records it.</summary>
    private sealed class RecordingFloorPlans : ISvcFloorPlans
    {
        public List<CalibrateFloorPlanRequest> Calibrations { get; } = [];

        public Task<Fluentx.Result<FloorPlanViewModel>> CalibrateAsync(Guid levelId, CalibrateFloorPlanRequest request, CancellationToken ct = default)
        {
            Calibrations.Add(request);
            return Task.FromResult(new Fluentx.Result<FloorPlanViewModel>(new FloorPlanViewModel(Guid.NewGuid(), levelId, "image/png", 1024, new string('a', 64), "plan.png",
                800, 600, request.MetresPerPixel, request.OriginX, request.OriginY, "DMO", null)));
        }

        public Task<Fluentx.Result<FloorPlanViewModel>> GetAsync(Guid levelId, CancellationToken ct = default) => Missing<FloorPlanViewModel>();

        public Task<Fluentx.Result<IReadOnlyList<FloorPlanViewModel>>> ListAsync(string siteCode, CancellationToken ct = default) => Missing<IReadOnlyList<FloorPlanViewModel>>();

        public Task<Fluentx.Result<FloorPlanContent>> OpenAsync(Guid levelId, CancellationToken ct = default) => Missing<FloorPlanContent>();

        public Task<Fluentx.Result<FloorPlanViewModel>> UploadAsync(Guid levelId, FloorPlanUpload upload, CancellationToken ct = default) => Missing<FloorPlanViewModel>();

        public Task<Fluentx.Result<bool>> DeleteAsync(Guid levelId, CancellationToken ct = default) => Missing<bool>();

        private static Task<Fluentx.Result<T>> Missing<T>() => Task.FromResult(Fluentx.Result.Error<T>(TopologyErrors.NotFound));
    }

    /// <summary>One device of zone "Snake A" at DMO.</summary>
    private sealed class OneDeviceGateway : ISvcDeviceGateway
    {
        public static readonly DeviceCredentials.Issued Device = DeviceCredentials.New();

        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult(
            prefix == Device.Prefix ? new DeviceCredentialRecord(Guid.NewGuid(), "S-17", "DMO", "Snake A", "Online", Device.Hash, [], null) : null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(Fluentx.Result.Error<DeviceZoneViewModel>("not used"));
    }

    #endregion
}
