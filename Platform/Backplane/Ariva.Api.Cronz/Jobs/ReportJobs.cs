using Ariva.Infra.Resilience;
using Ariva.Infra.Services.Reports;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The scheduled report deliveries (ARV-060, ADR-0020), every five minutes: one round of
/// <see cref="ReportDeliveryRound"/>: the due deliveries, each in its own scope and transaction. A round is idempotent (one delivery row
/// per schedule, day and recipient) and takes a transaction advisory lock, so a run from the dashboard, a second replica or
/// a restart never sends a report twice on purpose. A round ends at <see cref="RunTimeout"/> (ARV-096e): the deliveries it made
/// stay made (each committed on its own), and the next round takes the rest.
/// </summary>
public sealed class ReportJobs(ReportDeliveryRound round, ILogger<ReportJobs> logger, TimeProvider timeProvider = null) : IBoundedRuns
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Deliveries = "ReportDeliveries";

    /// <summary>Thirty minutes: 200 deliveries a round, each a daily report within its read bounds (seconds each).</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    [TickerFunction(Deliveries, "0 */5 * * * *")]
    public async Task DeliverAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = await RunBound.RunAsync(Deliveries, RunTimeout, timeProvider ?? TimeProvider.System, round.RunAsync, ct);
        if (run.Due > 0)
            logger.LogInformation("Report deliveries ({Type}): {Due} due, {Sent} sent, {Skipped} skipped, {Failed} failed", context.Type, run.Due, run.Sent, run.Skipped, run.Failed);
    }
}
