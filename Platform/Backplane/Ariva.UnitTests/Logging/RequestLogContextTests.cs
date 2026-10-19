using System.Net;
using System.Security.Claims;
using Ariva.Api.Common.Logging;
using Ariva.Api.Common.Security;
using FluentAssertions;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-091 (ASVS 5.0 V16.2.1): the request enricher names who (a user's id, an integration client, a device or a display,
/// anonymous, or unknown) and from where (the client address), with ids only: never a name, an email or a credential claim.
/// Its properties are its own (Caller...), so a line that names a target account in {UserId} and {SessionId} keeps the
/// target there and the caller apart.
/// </summary>
public sealed class RequestLogContextTests
{
    #region Helpers

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static LogEvent Write(RequestLogContext context, string template = "event", params object[] values)
    {
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using (LogContext.Push(context))
            logger.Information(template, values);
        return sink.Events.Single();
    }

    private static Dictionary<string, string> Properties(LogEvent logEvent) =>
        logEvent.Properties.ToDictionary(p => p.Key, p => (p.Value as ScalarValue)?.Value?.ToString());

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    #endregion

    [Fact]
    public void Enrich_Should_NameAnAnonymousCallerAndItsAddress_When_NothingIsAuthenticated()
    {
        var context = new RequestLogContext(IPAddress.Parse("::ffff:203.0.113.7"));

        var properties = Properties(Write(context));

        properties.Should().Equal(new Dictionary<string, string>
        {
            [RequestLogContext.CallerAddressProperty] = "203.0.113.7",
            [RequestLogContext.CallerUserIdProperty] = RequestLogContext.Anonymous
        });
        context.SourceKey.Should().Be("address:203.0.113.7", "an anonymous caller spends its address's throttle share");
    }

    [Fact]
    public void Enrich_Should_NameTheUserAndSessionById_When_AUserTokenIsAuthenticated()
    {
        var user = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        var context = new RequestLogContext(IPAddress.Parse("10.1.2.3"));

        context.Identify(Principal(new Claim("sub", user.ToString()), new Claim("sid", session.ToString()), new Claim("name", "officer.one"),
            new Claim("email", "officer.one@example.com"), new Claim("family", Guid.NewGuid().ToString())));
        var properties = Properties(Write(context));

        properties.Should().Equal(new Dictionary<string, string>
        {
            [RequestLogContext.CallerAddressProperty] = "10.1.2.3",
            [RequestLogContext.CallerUserIdProperty] = user.ToString(),
            [RequestLogContext.CallerSessionIdProperty] = session.ToString()
        }, "ids only: no name, email or token family");
        context.Identified.Should().BeTrue();
        context.SourceKey.Should().Be("user:" + user);
    }

    [Fact]
    public void Enrich_Should_NameTheIntegrationClient_When_AnIntegrationTokenIsAuthenticated()
    {
        var session = Guid.NewGuid();
        var context = new RequestLogContext(null);

        context.Identify(Principal(new Claim("sub", "ic_abcdefghijklmnopqrstuvwxyz"), new Claim("sid", session.ToString()), new Claim("scope", "flights:write")));
        var properties = Properties(Write(context));

        properties.Should().Equal(new Dictionary<string, string>
        {
            [RequestLogContext.CallerAddressProperty] = RequestLogContext.UnknownAddress,
            [RequestLogContext.CallerClientIdProperty] = "ic_abcdefghijklmnopqrstuvwxyz",
            [RequestLogContext.CallerSessionIdProperty] = session.ToString()
        });
    }

    [Fact]
    public void Enrich_Should_NameTheCheckedIntegrationClient_When_ThePrincipalIsAnonymous()
    {
        var context = new RequestLogContext(IPAddress.Loopback);

        context.Identify(new ClaimsPrincipal(new ClaimsIdentity()), "ic_abcdefghijklmnopqrstuvwxyz");

        Properties(Write(context)).Should().Contain(RequestLogContext.CallerClientIdProperty, "ic_abcdefghijklmnopqrstuvwxyz")
            .And.NotContainKey(RequestLogContext.CallerUserIdProperty);
    }

    [Fact]
    public void Enrich_Should_NameTheDeviceOrDisplay_When_AMachineCredentialIsAuthenticated()
    {
        var device = Guid.NewGuid();
        var display = Guid.NewGuid();
        var deviceContext = new RequestLogContext(IPAddress.Loopback);
        var displayContext = new RequestLogContext(IPAddress.Loopback);

        deviceContext.Identify(Principal(new Claim(DeviceAuthentication.DeviceIdClaim, device.ToString()), new Claim(DeviceAuthentication.DeviceCodeClaim, "S-17")));
        displayContext.Identify(Principal(new Claim(DisplayAuthentication.DisplayIdClaim, display.ToString()),
            new Claim(DisplayAuthentication.CredentialPrefixClaim, "ardp_prefix")));

        Properties(Write(deviceContext)).Should().Contain(RequestLogContext.CallerDeviceIdProperty, device.ToString())
            .And.NotContainKey(RequestLogContext.CallerUserIdProperty).And.HaveCount(2);
        Properties(Write(displayContext)).Should().Contain(RequestLogContext.CallerDisplayIdProperty, display.ToString())
            .And.NotContainKey(RequestLogContext.CallerUserIdProperty).And.HaveCount(2, "never the credential's prefix");
    }

    [Fact]
    public void Enrich_Should_NameAnUnknownCaller_When_TheSubjectIsNeitherAUserNorAClient()
    {
        var context = new RequestLogContext(IPAddress.Loopback);

        context.Identify(Principal(new Claim(ClaimTypes.Name, "someone@example.com"), new Claim("sub", "someone@example.com")));

        Properties(Write(context)).Should().Equal(new Dictionary<string, string>
        {
            [RequestLogContext.CallerAddressProperty] = "127.0.0.1",
            [RequestLogContext.CallerUserIdProperty] = RequestLogContext.Unknown
        }, "an authenticated caller is never left without a who, and its subject is not written");
    }

    [Fact]
    public void Enrich_Should_KeepTheTargetApartFromTheCaller_When_TheTemplateNamesAnotherAccountAndSession()
    {
        var administrator = Guid.CreateVersion7();
        var administratorSession = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        var targetSession = Guid.CreateVersion7();
        var context = new RequestLogContext(IPAddress.Parse("10.1.2.3"));
        context.Identify(Principal(new Claim("sub", administrator.ToString()), new Claim("sid", administratorSession.ToString())));

        var properties = Properties(Write(context, "Password of account {UserId} reset; session {SessionId} ended", target, targetSession));

        properties["UserId"].Should().Be(target.ToString());
        properties["SessionId"].Should().Be(targetSession.ToString());
        properties[RequestLogContext.CallerUserIdProperty].Should().Be(administrator.ToString());
        properties[RequestLogContext.CallerSessionIdProperty].Should().Be(administratorSession.ToString());
        properties[RequestLogContext.CallerAddressProperty].Should().Be("10.1.2.3");
    }

    [Fact]
    public void Enrich_Should_WinOverATemplatesOwnValue_When_ATemplateUsesACallerName()
    {
        var context = new RequestLogContext(IPAddress.Parse("10.1.2.3"));

        var properties = Properties(Write(context, "Spoofed {CallerAddress} {CallerUserId}", "198.51.100.1", "someone-else"));

        properties[RequestLogContext.CallerAddressProperty].Should().Be("10.1.2.3", "the Caller names are the enricher's own");
        properties[RequestLogContext.CallerUserIdProperty].Should().Be(RequestLogContext.Anonymous);
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("203.0.113.7", "::ffff:203.0.113.7", true)]
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    public void SourceKey_Should_ShareOneBudgetPerIpv6Network_When_AddressesAreInOneSlash64(string first, string second, bool shared)
    {
        var a = new RequestLogContext(IPAddress.Parse(first));
        var b = new RequestLogContext(IPAddress.Parse(second));

        (a.SourceKey == b.SourceKey).Should().Be(shared);
        Properties(Write(b))[RequestLogContext.CallerAddressProperty].Should().Be(IPAddress.Parse(second) is { IsIPv4MappedToIPv6: true } mapped
            ? mapped.MapToIPv4().ToString()
            : IPAddress.Parse(second).ToString(), "the logged address stays whole");
    }

    [Fact]
    public void MarkRecorded_Should_BeSeen_When_ASecurityEventIsRecorded()
    {
        var context = new RequestLogContext(IPAddress.Loopback);

        context.Recorded.Should().BeFalse();
        context.MarkRecorded();

        context.Recorded.Should().BeTrue();
    }
}
