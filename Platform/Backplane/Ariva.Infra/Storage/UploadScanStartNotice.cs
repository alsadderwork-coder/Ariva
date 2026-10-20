using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Storage;

/// <summary>
/// Says once, when a host without a malware scanner starts (ARV-092b), what that means for its uploads: in vm-local they
/// are taken unscanned, everywhere else every upload is refused until <c>MalwareScan:Host</c> names a clamd. Registered
/// only when no scanner is configured; k8s-prd does not start without one.
/// </summary>
internal sealed class UploadScanStartNotice(UploadScanPolicy policy, ILogger<UploadScanStartNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (policy.FailOpen)
            logger.LogWarning("Uploads are not scanned for malware: MalwareScan:Host is empty, which only vm-local allows");
        else
            logger.LogWarning("MalwareScan:Host is empty: every upload is refused until it names the clamd service");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
