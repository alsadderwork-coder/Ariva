using Ariva.Infra.Security;
using Ariva.Infra.Services.Foundation;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Services.Security;

/// <summary>
/// Ends a user's sessions and drops their cached permissions inside the caller's unit of work (ARV-010b, ARV-011).
/// The session rows change in one UPDATE; the cache entries are evicted after commit, so every node refuses the
/// user's access tokens within the session cache time and resolves the new grants on the next request.
/// </summary>
internal sealed class AccountSessions(IUnitOfWork unitOfWork, IFusionCache cache, ILogger<AccountSessions> logger) : SvcDb(unitOfWork)
{
    public static readonly EventId SessionsRevoked = new(9105, "SecurityEvent.SessionsRevoked");

    /// <summary>Revokes every active session of the user except <paramref name="keep"/> (Guid.Empty keeps none); returns how many.</summary>
    public async Task<int> RevokeAllAsync(Guid userId, DateTime now, string reason, Guid keep, CancellationToken ct = default)
    {
        var rows = await ExecuteCommandAsync<IdRow>(
            """
            UPDATE user_session SET revoked_on = :now, revoked_reason = :reason
             WHERE user_id = :userId AND revoked_on IS NULL AND id <> :keep
            RETURNING id AS "Id"
            """,
            new Dictionary<string, object> { ["now"] = now, ["reason"] = reason, ["userId"] = userId, ["keep"] = keep },
            ct);

        foreach (var row in rows)
        {
            var sessionId = row.Id;
            RegisterPostCommitAction(() => EvictSessionAsync(sessionId));
        }

        if (rows.Count > 0)
            logger.LogInformation(SessionsRevoked, "{Count} session(s) of account {UserId} revoked ({Reason})", rows.Count, userId, reason);
        return rows.Count;
    }

    /// <summary>After commit, drops the user's cached role grants so the next request reads the new ones.</summary>
    public void EvictPermissionsAfterCommit(Guid userId) =>
        RegisterPostCommitAction(() => cache.RemoveByTagAsync(StoredPermissionResolver.Tag(userId)).AsTask());

    public Task EvictSessionAsync(Guid sessionId) => cache.RemoveAsync(SessionValidator.Key(sessionId)).AsTask();

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }
}
