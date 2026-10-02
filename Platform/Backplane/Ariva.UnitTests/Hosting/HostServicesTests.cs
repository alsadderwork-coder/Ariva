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

    [Fact]
    public async Task Ingest_Should_ResolveTheDeviceGateway_When_ADeviceAuthenticates()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Ingest);
        await using var scope = app.Services.CreateAsyncScope();

        scope.ServiceProvider.GetService<ISvcDeviceGateway>().Should().NotBeNull("the device scheme looks the credential up on every request");
        scope.ServiceProvider.GetService<Ariva.Infra.Sensing.SensingIngest>().Should().NotBeNull();
    }
}
