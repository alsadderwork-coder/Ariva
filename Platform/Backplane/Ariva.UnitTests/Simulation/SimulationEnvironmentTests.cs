using Ariva.Api.Common.Hosting;
using Ariva.Simulation.Api.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>ARV-098: the simulator refuses to start without a known environment, as the Backplane hosts do.</summary>
public sealed class SimulationEnvironmentTests
{
    [Fact]
    public void Known_Should_EqualTheBackplaneEnvironments_When_EitherListChanges()
    {
        SimulationEnvironment.Known.Should().Equal(ArivaEnvironment.Known);
    }

    [Theory]
    [InlineData("--environment=k8s-dev", "k8s-dev")]
    [InlineData("--environment=VM-LOCAL", "vm-local")]
    public void Resolve_Should_ReturnTheCanonicalName_When_TheArgumentNamesAKnownEnvironment(string argument, string expected)
    {
        SimulationEnvironment.Resolve([argument], AppContext.BaseDirectory).Should().Be(expected);
    }

    [Fact]
    public void Resolve_Should_RefuseAnUnknownName_When_ItWouldSelectNoSettings()
    {
        var resolve = () => SimulationEnvironment.Resolve(["--environment=Production"], AppContext.BaseDirectory);

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*Unknown environment*");
    }

    [Fact]
    public void Resolve_Should_RefuseToStart_When_NothingNamesTheEnvironment()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is not null
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is not null)
        {
            Assert.Skip("An environment variable names the environment on this machine.");
        }

        var folder = Directory.CreateTempSubdirectory("ariva-sim-env-");
        try
        {
            var resolve = () => SimulationEnvironment.Resolve([], folder.FullName);

            resolve.Should().Throw<InvalidOperationException>().WithMessage("*DOTNET_ENVIRONMENT*");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
