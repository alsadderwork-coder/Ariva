using System.Reflection;
using Ariva.Api.Cronz.Jobs;
using Ariva.Core.Availability;
using Ariva.Di.Extensions;
using Ariva.Infra.Availability;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TickerQ.Utilities.Base;

namespace Ariva.UnitTests.Jobs;

/// <summary>
/// ARV-118: the availability ledger job in Ariva.Api.Cronz. It runs every minute; the ledger reads Redis snapshots and the
/// database only (no HTTP client of any kind can reach it, so it never calls another host); its settings are checked at
/// start.
/// </summary>
public sealed class AvailabilityJobsTests
{
    [Fact]
    public void Ledger_Should_TakeNoHttpClient_When_Constructed()
    {
        var parameters = typeof(AvailabilityLedger).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();

        parameters.Should().NotContain(t => typeof(HttpClient).IsAssignableFrom(t) || t == typeof(IHttpClientFactory) || t.Namespace == "System.Net.Http");
        parameters.Should().Contain(typeof(Ariva.Infra.Live.ILiveSnapshotStore));
        typeof(AvailabilityJobs).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Should().NotContain(t => typeof(HttpClient).IsAssignableFrom(t) || t == typeof(IHttpClientFactory));
    }

    [Fact]
    public void Job_Should_RunEveryMinute_When_Scheduled()
    {
        var function = typeof(AvailabilityJobs).GetMethod(nameof(AvailabilityJobs.RecordAsync)).CustomAttributes
            .Single(a => a.AttributeType == typeof(TickerFunctionAttribute));

        function.ConstructorArguments.Select(a => a.Value).Should().StartWith([AvailabilityJobs.Ledger, "30 * * * * *"]);
    }

    [Fact]
    public void AddArivaAvailabilityLedger_Should_RefuseTheHost_When_TheSettingsAreOutOfRange()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Availability:GraceSeconds"] = "5" }).Build();

        FluentActions.Invoking(() => new ServiceCollection().AddArivaAvailabilityLedger(configuration))
            .Should().Throw<InvalidOperationException>().WithMessage("*Availability:GraceSeconds*");
    }

    [Fact]
    public void AddArivaAvailabilityLedger_Should_BindTheSection_When_Valid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Availability:GraceSeconds"] = "30" }).Build();
        var services = new ServiceCollection().AddArivaAvailabilityLedger(configuration);

        services.Single(d => d.ServiceType == typeof(AvailabilitySettings)).ImplementationInstance.Should().BeOfType<AvailabilitySettings>()
            .Which.GraceSeconds.Should().Be(30);
        services.Should().Contain(d => d.ServiceType == typeof(AvailabilityLedger));
    }
}
