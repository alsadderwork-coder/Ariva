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
