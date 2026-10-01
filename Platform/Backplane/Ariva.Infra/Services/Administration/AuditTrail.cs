using System.Diagnostics;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Administration;

/// <summary>
/// Writes audit entries in the caller's unit of work (ARV-011), so an entry exists exactly when the change it
/// describes is committed. The actor, address and trace come from the request; summaries must never hold secrets.
/// </summary>
internal sealed class AuditTrail(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider) : SvcDb(unitOfWork)
{
    public Task RecordAsync(string action, string targetType, Guid? targetId, string targetName, string before, string after, CancellationToken ct = default)
    {
        var entry = new AuditEntry(
            timeProvider.GetUtcNow().UtcDateTime,
            currentUser.Id,
            currentUser.UserName,
            action,
            targetType,
            targetId,
            targetName,
            before,
            after,
            currentUser.GetCallerIpAddress(),
            Activity.Current?.TraceId.ToString());
        return SaveAsync(entry, ct);
    }

    /// <summary>The summary of a user the audit keeps: names, roles and state flags, never credentials.</summary>
    public static string Summary(User user) =>
        user is null
            ? null
            : $"userName={user.UserName}; displayName={user.DisplayName}; email={user.Email}; roles={string.Join(",", user.Roles.Select(r => r.RoleCode).Order(StringComparer.Ordinal))}; " +
              $"sites={(user.AllSites ? "*" : string.Join(",", user.Sites.Select(s => s.SiteCode).Order(StringComparer.Ordinal)))}; " +
              $"disabled={user.IsDisabled}; totp={user.TotpEnrolled}; mustChangePassword={user.MustChangePassword}";
}
