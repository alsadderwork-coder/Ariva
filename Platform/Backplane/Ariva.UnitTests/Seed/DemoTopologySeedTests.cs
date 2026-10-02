using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Services.Seed;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.UnitTests.Seed;

/// <summary>
/// ARV-019: zone profile v12 of the demo airport validates as built (23 zones, 39 lines, every queue with an entry and
/// an exit on its edge, every overflow feeding a queue), and the seed is refused in production.
/// </summary>
public sealed class DemoTopologySeedTests
{
    private static (Level Arrivals, Level Departures) Levels()
    {
        var terminal = new Airport("DMO", null, "Demo International Airport", "Asia/Dubai").AddTerminal("T1", "Terminal 1", "DMO");
        var arrivals = terminal.AddLevel("ARR", "Arrivals", 0, 100, 60);
        var departures = terminal.AddLevel("DEP", "Departures", 1, 100, 60);
        arrivals.Id = Guid.Parse("0199a000-0000-7000-8000-0000000000a1");
        departures.Id = Guid.Parse("0199a000-0000-7000-8000-0000000000d1");
        return (arrivals, departures);
    }

    [Fact]
    public void BuildProfile_Should_Validate_When_BuiltFromThePrototypeGeometry()
    {
        var (arrivals, departures) = Levels();

        var profile = DemoTopologySeed.BuildProfile(arrivals, departures);

        profile.Validate(new Dictionary<Guid, Level> { [arrivals.Id!.Value] = arrivals, [departures.Id!.Value] = departures })
            .Should().BeEmpty();
        profile.Zones.Should().HaveCount(23);
        profile.Zones.Count(z => z.Kind == ZoneKind.Queue).Should().Be(16);
        profile.Zones.Count(z => z.Kind == ZoneKind.Overflow).Should().Be(7);
        profile.Lines.Should().HaveCount(39);
        profile.Zones.Where(z => z.Kind == ZoneKind.Overflow).Should().OnlyContain(z => z.QueueZone != null && z.QueueZone.Kind == ZoneKind.Queue);
    }

    [Fact]
    public void BuildProfile_Should_PlaceArrivalQueuesOnArrivalsAndTheRestOnDepartures()
    {
        var (arrivals, departures) = Levels();

        var profile = DemoTopologySeed.BuildProfile(arrivals, departures);

        profile.Zones.Where(z => z.Name.StartsWith("A-", StringComparison.Ordinal)).Should().HaveCount(6).And.OnlyContain(z => z.LevelId == arrivals.Id);
        profile.Zones.Where(z => !z.Name.StartsWith("A-", StringComparison.Ordinal)).Should().OnlyContain(z => z.LevelId == departures.Id);
    }

    [Fact]
    public void BuildProfile_Should_HashTheSame_When_BuiltTwice()
    {
        var (arrivals, departures) = Levels();

        DemoTopologySeed.BuildProfile(arrivals, departures).ComputeGeometryHash()
            .Should().Be(DemoTopologySeed.BuildProfile(arrivals, departures).ComputeGeometryHash());
    }

    private static ServiceCollection Register(string hostEnvironment, string applicationEnvironment, bool on)
    {
        var values = new Dictionary<string, string> { [DemoSeedExtensions.SettingName] = on ? "true" : "false" };
        if (applicationEnvironment is not null)
            values["Application:Environment"] = applicationEnvironment;
        var services = new ServiceCollection();
        services.AddArivaDemoSeed(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), hostEnvironment);
        return services;
    }

    [Theory]
    [InlineData("k8s-prd", "k8s-prd")]
    [InlineData("k8s-prd", null)] // a prd settings file without Application:Environment, which then defaults to vm-local
    [InlineData("k8s-prd", "vm-local")]
    [InlineData("k8s-demo", "k8s-prd")]
    [InlineData("Production", "vm-local")]
    [InlineData("", "vm-local")]
    public void AddArivaDemoSeed_Should_Refuse_When_OnOutsideDevelopmentAndDemo(string hostEnvironment, string applicationEnvironment)
    {
        var act = () => Register(hostEnvironment, applicationEnvironment ?? "vm-local", on: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Seed:DemoTopology*");
    }

    [Theory]
    [InlineData("k8s-prd", "k8s-prd", false, false)]
    [InlineData("vm-local", "vm-local", false, false)]
    [InlineData("vm-local", "vm-local", true, true)]
    [InlineData("k8s-demo", "k8s-demo", true, true)]
    [InlineData("k8s-demo", "vm-local", true, true)]
    public void AddArivaDemoSeed_Should_RegisterTheHostedSeed_Only_When_On(string hostEnvironment, string applicationEnvironment, bool on, bool registered)
    {
        Register(hostEnvironment, applicationEnvironment, on).Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DemoSeedService))
            .Should().Be(registered);
    }

    private sealed class FlakySeed(int failures) : IDemoTopologySeed
    {
        public int Calls { get; private set; }

        public Task<SeedOutcome> RunAsync(CancellationToken ct) =>
            ++Calls <= failures ? throw new InvalidOperationException("database starting") : Task.FromResult(new SeedOutcome(7));
    }

    private sealed class NoUnitOfWork : IUnitOfWork
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IStorageProvider StorageProvider => null;
        public bool IsToBeCommitted { get; private set; }
        public int Ended { get; private set; }
        public Task EndAsync(CancellationToken ct = default) { Ended++; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void RegisterPostCommitAction(Func<Task> action) { }
        public void RegisterPostRollbackAction(Func<Task> action) { }
        public void PromiseToCommit() => IsToBeCommitted = true;
        public void PromiseNotToCommit() => IsToBeCommitted = false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(DemoSeedService Service, FlakySeed Seed)> RunServiceAsync(int failures, int retries)
    {
        var seed = new FlakySeed(failures);
        var services = new ServiceCollection();
        services.AddSingleton<IDemoTopologySeed>(seed);
        services.AddScoped<IUnitOfWork, NoUnitOfWork>();
        await using var provider = services.BuildServiceProvider();
        var service = new DemoSeedService(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DemoSeedService>.Instance, [.. Enumerable.Repeat(TimeSpan.Zero, retries)]);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        return (service, seed);
    }

    [Fact]
    public async Task DemoSeedService_Should_Retry_When_TheSeedFails()
    {
        var (service, seed) = await RunServiceAsync(failures: 2, retries: 3);

        seed.Calls.Should().Be(3);
        service.Outcome.Should().Be(new SeedOutcome(7));
    }

    [Fact]
    public async Task DemoSeedService_Should_GiveUpWithoutThrowing_When_EveryAttemptFails()
    {
        var (service, seed) = await RunServiceAsync(failures: 10, retries: 2);

        seed.Calls.Should().Be(3, "the first attempt and two retries");
        service.Outcome.Should().BeNull();
        service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("a demo convenience never stops the host");
    }
}
