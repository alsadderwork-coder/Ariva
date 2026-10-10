using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// Runs the demo topology seeds in the background once the host has started (after the development migration and the
/// schema gate, which start first), in Ariva.Api.Main of vm-local, k8s-dev and k8s-demo (<c>Seed:DemoTopology</c>):
/// Demo International Airport (DMO, ARV-019), then the illustrative AUH Terminal A arrivals (AUH-TA, ARV-139a), in the
/// order they are registered, each in its own scope and unit of work. A failure is logged and retried with backoff (a
/// database still starting, a pod racing another); after the last attempt the host goes on to the next seed and keeps
/// running without that demo data, which is a convenience, never a reason to stop the API.
/// </summary>
internal sealed class DemoSeedService(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<DemoSeedService> logger,
    IReadOnlyList<TimeSpan> retryDelays = null) : BackgroundService
{
    internal static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];

    private readonly IReadOnlyList<TimeSpan> _delays = retryDelays ?? DefaultRetryDelays;
    private readonly List<SeedOutcome> _outcomes = [];

    /// <summary>The outcome of the first seed's successful run, or null when every attempt failed.</summary>
    internal SeedOutcome Outcome => _outcomes.Count > 0 ? _outcomes[0] : null;

    /// <summary>Each seed's outcome in order, null for a seed whose every attempt failed.</summary>
    internal IReadOnlyList<SeedOutcome> Outcomes => _outcomes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int seeds;
        await using (var first = scopes.CreateAsyncScope())
            seeds = first.ServiceProvider.GetServices<IDemoTopologySeed>().Count();

        for (var index = 0; index < seeds && !stoppingToken.IsCancellationRequested; index++)
            _outcomes.Add(await RunWithRetriesAsync(index, stoppingToken));
    }

    private async Task<SeedOutcome> RunWithRetriesAsync(int index, CancellationToken stoppingToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var name = "?";
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var seed = scope.ServiceProvider.GetServices<IDemoTopologySeed>().ElementAt(index);
                name = seed.Name;
                var outcome = await seed.RunAsync(stoppingToken);
                await unitOfWork.EndAsync(stoppingToken);
                logger.LogInformation("Demo topology seed {Seed}: {Created} records created", name, outcome.Created);
                if (outcome.ProfileSkipped is not null)
                    logger.LogWarning("Demo topology seed {Seed} left its zone profile out: {Reason}", name, outcome.ProfileSkipped);
                return outcome;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return null;
            }
#pragma warning disable CA1031 // a demo convenience must not stop the API
            catch (Exception e)
#pragma warning restore CA1031
            {
                if (attempt >= _delays.Count)
                {
                    logger.LogError(e, "Demo topology seed {Seed} failed after {Attempts} attempts; the API runs without its demo data", name, attempt + 1);
                    return null;
                }

                logger.LogWarning(e, "Demo topology seed {Seed} attempt {Attempt} failed; retrying in {Delay}", name, attempt + 1, _delays[attempt]);
            }

            try
            {
                await Task.Delay(_delays[attempt], timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }
}
