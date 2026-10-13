using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-156: the start gate of the in-process hosts (<see cref="GatedHostLifetime"/>). A host's entry point starts the host on
/// its own thread while <c>WebApplicationFactory</c> subscribes to that start; the gate holds the start until the factory has
/// subscribed, so a start that fails is always reported as its own exception, never as a disposed service provider.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class HostStartGateTests
{
    private sealed class RefusingService(TaskCompletionSource reached) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            reached.TrySetResult();
            throw new InvalidOperationException("Refused at start.");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task WaitForStartAsync_Should_HoldTheStartUntilTheGateOpens_When_AHostStarts()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddHostedService(_ => new RefusingService(reached));
        GatedHostLifetime.Hold(builder.Services, gate.Task);
        using var host = builder.Build();

        var start = host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IHostLifetime>().Should().BeOfType<GatedHostLifetime>();
        start.IsCompleted.Should().BeFalse("the start waits at the gate, before the hosted services");
        reached.Task.IsCompleted.Should().BeFalse("no hosted service starts before the gate opens");

        gate.SetResult();

        await FluentActions.Awaiting(() => start).Should().ThrowAsync<InvalidOperationException>().WithMessage("Refused at start.");
        reached.Task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Create_Should_GateTheHostsStart_When_AHostIsBooted()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Cronz);

        app.Services.GetRequiredService<IHostLifetime>().Should().BeOfType<GatedHostLifetime>();
    }
}
