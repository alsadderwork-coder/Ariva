using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// Runs the demo topology seed in the background once the host has started (after the development migration and the
/// schema gate, which start first), in Ariva.Api.Main of vm-local, k8s-dev and k8s-demo (<c>Seed:DemoTopology</c>).
/// A failure is logged and retried with backoff (a database still starting, a pod racing another); after the last
/// attempt the host keeps running without the demo data, which is a convenience, never a reason to stop the API.
/// </summary>
internal sealed class DemoSeedService(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<DemoSeedService> logger,
    IReadOnlyList<TimeSpan> retryDelays = null) : BackgroundService
{
    internal static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];

    private readonly IReadOnlyList<TimeSpan> _delays = retryDelays ?? DefaultRetryDelays;

    /// <summary>The outcome of the successful run, or null when every attempt failed.</summary>
    internal SeedOutcome Outcome { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var outcome = await scope.ServiceProvider.GetRequiredService<IDemoTopologySeed>().RunAsync(stoppingToken);
                await unitOfWork.EndAsync(stoppingToken);
                Outcome = outcome;
                logger.LogInformation("Demo topology seed: {Created} records created", outcome.Created);
                if (outcome.ProfileSkipped is not null)
                    logger.LogWarning("Demo topology seed left zone profile v12 out: {Reason}", outcome.ProfileSkipped);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a demo convenience must not stop the API
            catch (Exception e)
#pragma warning restore CA1031
            {
                if (attempt >= _delays.Count)
                {
                    logger.LogError(e, "Demo topology seed failed after {Attempts} attempts; the API runs without the demo data", attempt + 1);
                    return;
                }

                logger.LogWarning(e, "Demo topology seed attempt {Attempt} failed; retrying in {Delay}", attempt + 1, _delays[attempt]);
            }

            try
            {
                await Task.Delay(_delays[attempt], timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
