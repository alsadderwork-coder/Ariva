using System.Text.Json;
using Ariva.Core.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Infra.Imports;

/// <summary>
/// The audit entries of the CSV imports (ARV-123a): the kind, the outcome, a fixed reason, the file's SHA-256 and the
/// counts; never a cell, a code from the file or a credential (CWE-532).
/// </summary>
internal static class ImportAudit
{
    public static string Summary(string kind, string outcome, string reason, string fileSha256, int rows, int valid, int errors, int created) =>
        JsonSerializer.Serialize(new { kind, outcome, reason, fileSha256, rows, valid, errors, created }, JsonSerializerOptions.Web);

    /// <summary>
    /// An audit entry in a transaction of its own, as the request's caller (the new scope is not the request's): a preview
    /// is always rolled back and a refused commit keeps nothing, yet both are recorded. Throws when it cannot be written.
    /// </summary>
    public static async Task WriteOwnAsync(IServiceScopeFactory scopes, ICurrentUser caller, TimeProvider timeProvider, string action, string siteCode, string summary)
    {
        await using var scope = scopes.CreateAsyncScope();
        var auditUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            await new AuditTrail(auditUnitOfWork, caller, timeProvider).RecordAsync(action, "Site", null, siteCode, null, summary, CancellationToken.None);
            auditUnitOfWork.PromiseToCommit();
            await auditUnitOfWork.EndAsync(CancellationToken.None);
        }
        catch
        {
            await auditUnitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
