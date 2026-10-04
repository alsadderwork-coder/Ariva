using Ariva.Infra.Services.Reports;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The scheduled report deliveries (ARV-060, ADR-0020), every five minutes: one round of
/// <see cref="ReportDeliveryRound"/>: the due deliveries, each in its own scope and transaction. A round is idempotent (one delivery row
/// per schedule, day and recipient) and takes a transaction advisory lock, so a run from the dashboard, a second replica or
/// a restart never sends a report twice on purpose.
/// </summary>
public sealed class ReportJobs(ReportDeliveryRound round, ILogger<ReportJobs> logger)
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Deliveries = "ReportDeliveries";

    [TickerFunction(Deliveries, "0 */5 * * * *")]
    public async Task DeliverAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = await round.RunAsync(ct);
        if (run.Due > 0)
            logger.LogInformation("Report deliveries ({Type}): {Due} due, {Sent} sent, {Skipped} skipped, {Failed} failed", context.Type, run.Due, run.Sent, run.Skipped, run.Failed);
    }
}
