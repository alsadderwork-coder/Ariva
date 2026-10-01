using System.Net;
using Ariva.Api.Common.Hosting;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

    #endregion

    #region Helpers

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
