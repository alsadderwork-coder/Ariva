using Ariva.Core.Security;
using Ariva.Infra.Settings;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Security;

/// <summary>
/// The per-request session check (ARV-010b). The session's deadlines and revocation are cached for
/// <see cref="SessionSettings.CacheSeconds"/> in memory (4 seconds) and longer in Redis; revoking a session removes the
/// entry everywhere through the backplane, and without one the memory copy expires within the cache time. Deadlines
/// are compared at check time, so a cached copy never extends a session.
/// </summary>
internal sealed class SessionValidator(IUnitOfWork unitOfWork, IFusionCache cache, AuthSettings settings, TimeProvider timeProvider) : ISessionValidator
{
    public static string Key(Guid sessionId) => $"session:{sessionId:N}";

    public async Task<SessionState> CheckAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            return SessionState.Unknown;

        var snapshot = await cache.GetOrSetAsync<SessionSnapshot>(
            Key(sessionId),
            async (_, token) =>
            {
                var row = await unitOfWork.StorageProvider.Query<UserSession>()
                    .Where(s => s.Id == sessionId)
                    .Select(s => new { s.IdleExpiresOn, s.AbsoluteExpiresOn, s.RevokedOn, s.User.IsDisabled })
                    .FirstOrDefaultAsync(token);
                return row is null ? null : new SessionSnapshot(row.IdleExpiresOn, row.AbsoluteExpiresOn, row.RevokedOn is not null || row.IsDisabled);
            },
            options => options
                .SetDuration(TimeSpan.FromSeconds(settings.Sessions.CacheSeconds))
                .SetDistributedCacheDuration(TimeSpan.FromMinutes(5)),
            token: ct);

        if (snapshot is null)
            return SessionState.Unknown;
        if (snapshot.Revoked)
            return SessionState.Revoked;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        return now >= snapshot.IdleExpiresOn || now >= snapshot.AbsoluteExpiresOn ? SessionState.Expired : SessionState.Active;
    }
}

/// <summary>What the session check caches; serialised to Redis as JSON.</summary>
public sealed record SessionSnapshot(DateTime IdleExpiresOn, DateTime AbsoluteExpiresOn, bool Revoked);
