using Ariva.Api.Common.Hosting;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace Ariva.UnitTests.Security;

/// <summary>
/// The simulator fabricates sensor, AODB and AMAN data, so it refuses to start in production and exits non-zero
/// before it builds a host.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SimulationGuardTests
{
    [Fact]
    public async Task CreateClient_Should_Fail_When_SimulationStartsInK8sPrd()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, ArivaEnvironment.K8sPrd);

        Action start = () => app.CreateClient();

        start.Should().Throw<InvalidOperationException>("the entry point returns before it builds a host");
    }

    [Fact]
    public async Task CreateClient_Should_Fail_When_SimulationConfigurationSaysK8sPrd()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
            builder.UseSetting("Application:Environment", ArivaEnvironment.K8sPrd));

        Action start = () => app.CreateClient();

        start.Should().Throw<InvalidOperationException>("a production configuration file is enough to refuse");
    }

    /// <summary>
    /// ARV-104i: a configuration the simulator accepts (an in-cluster Ariva.Api.Main over plain HTTP with AllowInsecureTransport,
    /// one observer account), so that in production only the guard refuses it.
    /// </summary>
    private static void ObserverAccounts(IWebHostBuilder builder)
    {
        builder.UseSetting("Simulation:Ariva:MainUrl", "http://api-main-service");
        builder.UseSetting("Simulation:Ariva:AllowInsecureTransport", "true");
        builder.UseSetting("Simulation:Validation:Observers:0:UserName", "observer.one");
        builder.UseSetting("Simulation:Validation:Observers:0:Password", "observer-one-password-0001");
    }

    [Fact]
    public async Task CreateClient_Should_Fail_When_SimulationStartsInK8sPrdWithObserverAccounts()
    {
        // ARV-104i: the validation observers sign in to Ariva as real accounts; with their credentials configured (validly: the
        // same configuration starts in k8s-dev, below) the guard still refuses before any service, client or sign-in exists.
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, ArivaEnvironment.K8sPrd, ObserverAccounts);

        Action start = () => app.CreateClient();

        start.Should().Throw<InvalidOperationException>("the entry point returns before it builds a host, whatever the emulators are given");
    }

    [Fact]
    public async Task CreateClient_Should_Start_When_TheSameObserverAccountsAreConfiguredOutsideProduction()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, ArivaEnvironment.K8sDev, ObserverAccounts);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/readiness", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(200, "the configuration is valid: in production only the guard refuses it");
    }

    [Theory]
    [InlineData(ArivaEnvironment.VmLocal)]
    [InlineData(ArivaEnvironment.K8sDev)]
    [InlineData(ArivaEnvironment.K8sDemo)]
    public async Task CreateClient_Should_Start_When_SimulationRunsOutsideProduction(string environment)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, environment);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/readiness", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(200);
    }
}
