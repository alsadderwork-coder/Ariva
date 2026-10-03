using Ariva.Core.Services;
using Ariva.Core.Services.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ariva.Infra.Integration;

/// <summary>
/// Deletes expired idempotency keys of Integration API batches (ARV-043, script 0026) every ten minutes, in rounds of
/// at most 5,000 rows so a backlog never holds a long transaction. Every replica may sweep: a key is deleted once,
/// whichever replica gets there first.
/// </summary>
internal sealed class IdempotencySweeper(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<IdempotencySweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public const int RoundSize = 5_000;
    public const int MaxRounds = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                var total = 0;
                for (var round = 0; round < MaxRounds; round++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var deleted = await scope.ServiceProvider.GetRequiredService<ISvcIntegrationIdempotency>().SweepAsync(RoundSize, stoppingToken);
                    unitOfWork.PromiseToCommit();
                    await unitOfWork.EndAsync(stoppingToken);
                    total += deleted;
                    if (deleted < RoundSize)
                        break;
                }

                if (total > 0)
                    logger.LogInformation("Deleted {Count} expired idempotency keys", total);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one failed sweep (the database restarting) must not stop the sweeper; the next one retries
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Idempotency key sweep failed; retrying at the next interval");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
