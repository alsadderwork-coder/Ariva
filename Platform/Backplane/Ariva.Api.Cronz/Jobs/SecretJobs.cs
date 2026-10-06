using Ariva.Infra.DataProtection;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// Re-protects the stored secrets (user and integration client TOTP seeds, outbound endpoint secrets) under the current
/// Data Protection key, daily at 02:40 UTC (ARV-080, ASVS V11.2.2). Only values under an older key are rewritten, each
/// with a compare-and-swap UPDATE, so a run from the dashboard or a second replica is harmless.
/// </summary>
public sealed class SecretJobs(SecretReprotectionRound round, ILogger<SecretJobs> logger)
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Reprotect = "SecretReprotection";

    [TickerFunction(Reprotect, "0 40 2 * * *")]
    public async Task ReprotectAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = await round.RunAsync(ct);
        if (run.Reprotected > 0 || run.Failed > 0)
            logger.LogInformation("Secret re-protection ({Type}): {Checked} checked, {Reprotected} re-protected, {Failed} unreadable", context.Type, run.Checked, run.Reprotected, run.Failed);
    }
}
