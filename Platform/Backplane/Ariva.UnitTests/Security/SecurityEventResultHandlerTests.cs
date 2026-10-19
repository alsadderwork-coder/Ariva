using Ariva.Api.Common.Logging;
using Ariva.Api.Common.Security;
using Ariva.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-091 (ASVS 5.0 V16.3.2): the authorization middleware's refusals are logged by a decorator that hands every decision to
/// the step-up handler unchanged, so each answer is the same byte for byte; the event names the reason and the failed
/// requirements as the code names them, never anything from the request.
/// </summary>
public sealed class SecurityEventResultHandlerTests
{
    #region Fixtures

    private sealed class CapturedLogger : ILogger<SecurityEventLog>
    {
        public List<(EventId Id, LogLevel Level, IReadOnlyDictionary<string, object> Values)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : [];
            Entries.Add((eventId, logLevel, values));
        }
    }

    private static readonly AuthorizationPolicy Authenticated = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    public static TheoryData<string> Outcomes => new() { "challenge", "permission", "step-up", "permission and step-up", "success" };

    #endregion

    #region Helpers

    private static DefaultHttpContext Context()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(ArivaAuthenticationSchemes.Deny)
            .AddScheme<AuthenticationSchemeOptions, DenyAuthenticationHandler>(ArivaAuthenticationSchemes.Deny, null, null);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            TraceIdentifier = "trace-fixed",
            Response = { Body = new MemoryStream() },
            Request = { Method = "POST", Path = "/api/v1/admin/users", QueryString = new QueryString("?text=<script>canary-4411") }
        };
    }

    private static PolicyAuthorizationResult Outcome(string name) => name switch
    {
        "challenge" => PolicyAuthorizationResult.Challenge(),
        "permission" => PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed(
            [new PermissionRequirement([Global.Defaults.Permissions.CreateUser, Global.Defaults.Permissions.EditUser])])),
        "step-up" => PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed([new RecentMfaRequirement(TimeSpan.FromMinutes(15))])),
        "permission and step-up" => PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed(
            [new RecentMfaRequirement(TimeSpan.FromMinutes(15)), new PermissionRequirement([Global.Defaults.Permissions.CreateUser])])),
        _ => PolicyAuthorizationResult.Success()
    };

    private static async Task<(int Status, string Headers, byte[] Body, bool NextCalled)> AnswerAsync(IAuthorizationMiddlewareResultHandler handler, string outcome)
    {
        var context = Context();
        var nextCalled = false;
        await handler.HandleAsync(_ => { nextCalled = true; return Task.CompletedTask; }, context, Authenticated, Outcome(outcome));
        var headers = string.Join("\n", context.Response.Headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}: {h.Value}"));
        return (context.Response.StatusCode, headers, ((MemoryStream)context.Response.Body).ToArray(), nextCalled);
    }

    #endregion

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task HandleAsync_Should_AnswerExactlyAsTheStepUpHandler_When_ItWrapsIt(string outcome)
    {
        using var events = new SecurityEventLog(new CapturedLogger());
        var decorated = new SecurityEventResultHandler(new StepUpResultHandler(), events);

        var expected = await AnswerAsync(new StepUpResultHandler(), outcome);
        var actual = await AnswerAsync(decorated, outcome);

        actual.Status.Should().Be(expected.Status, outcome);
        actual.Headers.Should().Be(expected.Headers, outcome);
        actual.Body.Should().Equal(expected.Body, outcome);
        actual.NextCalled.Should().Be(expected.NextCalled, outcome);
    }

    [Theory]
    [InlineData("challenge", SecurityEvents.AuthorizationChallengedId, SecurityEvents.NotAuthenticated, null)]
    [InlineData("step-up", SecurityEvents.AuthorizationChallengedId, SecurityEvents.SecondFactorRequired, null)]
    [InlineData("permission", SecurityEvents.AuthorizationForbiddenId, SecurityEvents.PermissionMissing, "Permission User.Create|User.Edit")]
    [InlineData("permission and step-up", SecurityEvents.AuthorizationForbiddenId, SecurityEvents.PermissionMissing, "SecondFactor, Permission User.Create")]
    public async Task HandleAsync_Should_WriteOneEventWithTheReason_When_TheRequestIsRefused(string outcome, int eventId, string reason, string requirement)
    {
        var logger = new CapturedLogger();
        using var events = new SecurityEventLog(logger);
        var handler = new SecurityEventResultHandler(new StepUpResultHandler(), events);

        await AnswerAsync(handler, outcome);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Id.Id.Should().Be(eventId);
        entry.Level.Should().Be(LogLevel.Warning, "Warning passes the Microsoft override and reaches every sink");
        entry.Values["Reason"].Should().Be(reason);
        entry.Values["Method"].Should().Be("POST");
        entry.Values["Endpoint"].Should().Be(SecurityEventLog.NoEndpoint, "no endpoint is set here, and the path is never logged");
        if (requirement is not null)
            entry.Values["Requirement"].Should().Be(requirement);
        string.Join(" ", entry.Values.Values).Should().NotContain("canary").And.NotContain("script").And.NotContain("/api/v1/admin/users");
    }

    [Fact]
    public async Task HandleAsync_Should_WriteNothing_When_TheRequestIsAuthorized()
    {
        var logger = new CapturedLogger();
        using var events = new SecurityEventLog(logger);
        var handler = new SecurityEventResultHandler(new StepUpResultHandler(), events);

        await AnswerAsync(handler, "success");

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_Should_AnswerTheSame403_When_TheEventCannotBeBuilt()
    {
        // A requirement the event cannot describe (no permissions at all): building the event throws inside the event log,
        // after the inner handler has answered; the answer must stay the 403, never a 500.
        var broken = PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed([new PermissionRequirement(null)]));
        using var metrics = new SecurityEventMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);
        using var events = new SecurityEventLog(new CapturedLogger(), metrics);
        var context = Context();

        var handle = () => new SecurityEventResultHandler(new StepUpResultHandler(), events).HandleAsync(_ => Task.CompletedTask, context, Authenticated, broken);

        await handle.Should().NotThrowAsync();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        reader.Sum(SecurityEventMetrics.EventsName, ("outcome", SecurityEventMetrics.Failed)).Should().Be(1);
    }

    [Fact]
    public void RequirementsOf_Should_NameClaimsScopesAndOtherRequirements_When_TheyFail()
    {
        var failure = AuthorizationFailure.Failed(
        [
            new ClaimsAuthorizationRequirement(DeviceAuthentication.DeviceIdClaim, null),
            new IntegrationScopeRequirement("flights:write"),
            new DeviceZoneRequirement(),
            new DenyAnonymousAuthorizationRequirement()
        ]);

        SecurityEventResultHandler.RequirementsOf(failure).Should().Be("Claim ariva:device_id, Scope flights:write, DeviceZoneRequirement, AuthenticatedUser");
        SecurityEventResultHandler.ReasonOf(failure).Should().Be(SecurityEvents.ScopeMissing);
        SecurityEventResultHandler.RequirementsOf(AuthorizationFailure.ExplicitFail()).Should().Be("Failed");
        SecurityEventResultHandler.ReasonOf(AuthorizationFailure.ExplicitFail()).Should().Be(SecurityEvents.PolicyNotMet);
    }

    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("DELETE", "DELETE")]
    [InlineData("get", SecurityEventLog.OtherMethod)]
    [InlineData("<SCRIPT>", SecurityEventLog.OtherMethod)]
    [InlineData("CANARYMETHOD", SecurityEventLog.OtherMethod)]
    public void MethodOf_Should_NameOnlyHttpsOwnMethods_When_AClientSendsAnyToken(string method, string expected)
    {
        var context = new DefaultHttpContext { Request = { Method = method } };

        SecurityEventLog.MethodOf(context).Should().Be(expected);
    }
}
