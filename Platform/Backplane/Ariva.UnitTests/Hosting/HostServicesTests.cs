using System.Reflection;
using Ariva.Core.Services.Sensing;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// Every controller and hub a host maps can be built from the host's own registrations. Tests that replace a service
/// with a fake also add it, so a registration missing from a host shows only here (or as a 500 in production).
/// </summary>
public sealed class HostServicesTests
{
    private static Assembly AssemblyOf(string host) => host switch
    {
        ArivaHosts.Main => typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        ArivaHosts.Ingest => typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
        ArivaHosts.Stream => typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
        ArivaHosts.Cronz => typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        ArivaHosts.Integration => typeof(Ariva.Api.Integration._IAssemblyMark).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(host))
    };

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_ResolveEveryControllerAndHubDependency_When_ItStarts(string host)
    {
        await using var app = ArivaHosts.Create(host);
        var known = app.Services.GetRequiredService<IServiceProviderIsService>();
        var endpoints = AssemblyOf(host).GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && (typeof(ControllerBase).IsAssignableFrom(t) || typeof(Hub).IsAssignableFrom(t)));

        var missing = (from type in endpoints
                       from parameter in type.GetConstructors().SelectMany(c => c.GetParameters())
                       where !parameter.HasDefaultValue && !known.IsService(parameter.ParameterType)
                       select $"{type.Name}({parameter.ParameterType.Name})").ToList();

        missing.Should().BeEmpty($"every service a {host} endpoint takes is registered by {host} itself");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_BuildEveryHostedService_When_ItStarts(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var build = () => app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();

        build.Should().NotThrow($"every worker {host} hosts is built from {host}'s own registrations");
    }

    [Fact]
    public async Task Integration_Should_SendTheAlertEmails_When_EmailIsOn()
    {
        // ARV-040: every host writes alert emails with the alert change; only Integration, the host with outbound egress, sends them.
        await using var integration = ArivaHosts.Create(ArivaHosts.Integration);
        await using var main = ArivaHosts.Create(ArivaHosts.Main);
        await using var scope = integration.Services.CreateAsyncScope();

        integration.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().ContainSingle(s => s is Ariva.Infra.Notifications.EmailSenderWorker);
        scope.ServiceProvider.GetService<Ariva.Infra.Notifications.EmailSender>().Should().NotBeNull();
        main.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().NotContain(s => s is Ariva.Infra.Notifications.EmailSenderWorker);
        await using var mainScope = main.Services.CreateAsyncScope();
        mainScope.ServiceProvider.GetService<Ariva.Infra.Notifications.AlertEmails>().Should().NotBeNull();
        mainScope.ServiceProvider.GetService<Ariva.Infra.Notifications.IEmailTransport>().Should().BeNull("Main never talks to the mail relay");
    }

    [Fact]
    public async Task Stream_Should_ResolveWhatTheAlertEvaluationRunsWith_When_ItTicks()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Stream);
        await using var scope = app.Services.CreateAsyncScope();

        // ARV-038: resolved from a scope per rule at every tick, not through a constructor, so only this shows a gap.
        app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().Contain(s => s is Ariva.Infra.Alerting.AlertEvaluationWorker);
        scope.ServiceProvider.GetService<Ariva.Infra.Alerting.AlertRuleTick>().Should().NotBeNull();
        scope.ServiceProvider.GetService<Ariva.Infra.Alerting.AlertInputs>().Should().NotBeNull();
        scope.ServiceProvider.GetService<Ariva.Core.Alerting.IArrivalWaveSource>().Should().NotBeNull();
        // ARV-187 (lead decision P3): one not-evaluated warning per rule and values version in the host, so the memory outlives each tick's scope.
        await using var next = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Ariva.Infra.Alerting.NotEvaluatedWarnings>()
            .Should().BeSameAs(next.ServiceProvider.GetRequiredService<Ariva.Infra.Alerting.NotEvaluatedWarnings>());
        await using var main = ArivaHosts.Create(ArivaHosts.Main);
        await using var mainScope = main.Services.CreateAsyncScope();
        mainScope.ServiceProvider.GetService<Ariva.Infra.Alerting.AlertRuleTick>().Should().NotBeNull("Main registers the tick too (RegisterArivaServices)");
    }

    [Fact]
    public async Task Stream_Should_GiveTheDeskFeedTheStreamsTimingOfEachSite_When_ItBuildsTheFeed()
    {
        // ARV-149a (R-g of its security re-check): the desk feed takes the stream settings the Stream host registers
        // (AddArivaQueueStream), so a queue minute's length falls due after the site's own stream timing; built without them the
        // feed would take the defaults, 180 s, whatever a site's timing.
        await using var app = ArivaHosts.Create(ArivaHosts.Stream, configure: builder => builder.UseSetting("Stream:Sites:XS1:LatenessSeconds", "120"));
        var stream = app.Services.GetRequiredService<Ariva.Infra.Streaming.StreamSettings>();
        var feed = app.Services.GetRequiredService<Ariva.Infra.Border.DeskFeed>();

        stream.TimingOf("XS1").LatenessSeconds.Should().Be(120);
        feed.QueueLengthDueFor("XS1").Should().Be(TimeSpan.FromSeconds(120 + stream.TimingOf("XS1").PushIntervalSeconds + stream.IdleTickSeconds
            + Ariva.Infra.Border.DeskFeed.QueueLengthWriteSeconds));
        feed.QueueLengthDueFor("XS2").Should().Be(Ariva.Infra.Border.DeskFeed.QueueLengthDue(stream, "XS2"))
            .And.NotBe(feed.QueueLengthDueFor("XS1"), "another site keeps the stream's default timing");
    }

    [Fact]
    public async Task Stream_Should_ReportEachSitesAmanFeedAgeOnTheBorderMeter_When_ItBuildsTheFeed()
    {
        // ARV-257: the desk feed takes its optional feed ages from the container, so they must be registered there (else the feed
        // is built without them and no site's age is ever reported), once, on the host's Ariva.Border meter.
        await using var app = ArivaHosts.Create(ArivaHosts.Stream);
        var ages = app.Services.GetService<Ariva.Infra.Border.AmanFeedAges>();
        using var reader = new InstrumentReader(app.Services.GetRequiredService<Ariva.Infra.Border.BorderMetrics>().Meter);

        ages.Should().NotBeNull("the desk feed's ages are registered with it (AddArivaDeskFeed)");
        app.Services.GetRequiredService<Ariva.Infra.Border.DeskFeed>().Should().NotBeNull();
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        ages.Listed([("XS1", now.AddMinutes(-4))], now);
        reader.Observe(Ariva.Infra.Border.BorderMetrics.FeedAgeName).Should().ContainSingle().Which.Value.Should().Be(240);
    }

    [Fact]
    public async Task Ingest_Should_ResolveTheDeviceGateway_When_ADeviceAuthenticates()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Ingest);
        await using var scope = app.Services.CreateAsyncScope();

        scope.ServiceProvider.GetService<ISvcDeviceGateway>().Should().NotBeNull("the device scheme looks the credential up on every request");
        scope.ServiceProvider.GetService<Ariva.Infra.Sensing.SensingIngest>().Should().NotBeNull();
    }
}
