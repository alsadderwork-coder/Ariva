using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Security;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Services.Security;

/// <summary>
/// Username and password sign-in with server-side sessions (ADR-0026, ARV-010a and ARV-010b).
/// <list type="bullet">
/// <item>Every sign-in failure returns <see cref="ISvcAuthenticator.InvalidCredentials"/> after one full PBKDF2
/// verification, including unknown users (verified against a dummy hash), disabled and locked accounts (CWE-204,
/// CWE-208).</item>
/// <item>Ten consecutive failures lock the account for 15 minutes (both configurable); the lock expires on its own and
/// is logged as a security event. Failures are counted atomically in the database, so parallel attempts from many
/// addresses cannot get past the threshold.</item>
/// <item>Every sign-in starts a new session and refresh family; a refresh cookie sent with the sign-in is revoked
/// (CWE-384). Refresh tokens are used once: the claim is one UPDATE whose row lock orders two tabs refreshing together,
/// and the second gets the same successor once within the grace window. Any other reuse revokes the family.</item>
/// <item>Revocation (logout, reuse, disable, password change) evicts the cached session after commit, so every node
/// refuses the access token within the session cache time.</item>
/// <item>Nothing secret is logged: no password and no token, access or refresh (CWE-532).</item>
/// </list>
/// The per-address limit (10 a minute) is the "auth" rate limiting policy on the endpoints.
/// </summary>
internal sealed class SvcAuthenticator(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    AuthSettings settings,
    PasswordPolicy passwordPolicy,
    AccessTokenIssuer issuer,
    IDataProtectionProvider dataProtection,
    IFusionCache cache,
    ILogger<SvcAuthenticator> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAuthenticator
{
    public static readonly EventId AccountLocked = new(9101, "SecurityEvent.AccountLocked");
    public static readonly EventId AccountUnlocked = new(9102, "SecurityEvent.AccountUnlocked");
    public static readonly EventId PasswordChanged = new(9103, "SecurityEvent.PasswordChanged");
    public static readonly EventId RefreshTokenReused = new(9104, "SecurityEvent.RefreshTokenReused");
    public static readonly EventId SessionsRevoked = new(9105, "SecurityEvent.SessionsRevoked");
    public static readonly EventId AccountDisabled = new(9106, "SecurityEvent.AccountDisabled");
    public static readonly EventId AccountEnabled = new(9107, "SecurityEvent.AccountEnabled");

    // Verified when the username is unknown, so the response takes as long as a real check.
    private static readonly Lazy<PasswordHashValue> DummyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString("N")));

    private static readonly string[] PasswordMethod = ["pwd"];

    private readonly IDataProtector _successors = dataProtection.CreateProtector(RefreshTokens.SuccessorPurpose);

    #region Sign-in

    public async Task<Result<SignInResult>> LoginAsync(LoginRequest request, SignInContext context, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrEmpty(request.UserName) || string.IsNullOrEmpty(request.Password))
            return Failed();

        var userName = UserNames.Normalize(request.UserName);
        var user = UserNames.IsValid(userName)
            ? await Query<User>().FirstOrDefaultAsync(u => u.UserName == userName, ct)
            : null;

        if (user is null)
        {
            PasswordHasher.Verify(request.Password, DummyHash.Value, out _);
            return Failed();
        }

        var now = UtcNow;
        var passwordMatches = PasswordHasher.Verify(request.Password, user.StoredPassword(), out var needsRehash);

        if (user.IsDisabled || user.IsLocked(now))
            return Failed();

        if (!passwordMatches)
        {
            await RecordFailureAsync(user.Id.Value, now, ct);
            return Failed();
        }

        if (needsRehash)
            user.SetPassword(PasswordHasher.Hash(request.Password), user.MustChangePassword);
        user.RecordSuccessfulLogin(now);
        await UpdateAsync(user, ct);

        // CWE-384: a session the browser still holds is ended, never continued.
        await RevokePresentedAsync(context?.PresentedRefreshToken, now, "replaced-by-login", ct);

        var session = new UserSession(user, now, PasswordMethod, IdleTimeout(user),
            TimeSpan.FromSeconds(settings.Sessions.AbsoluteSeconds), context?.IpAddress, context?.UserAgent);
        await SaveAsync(session, ct);
        var (refreshToken, _) = await IssueRefreshTokenAsync(session, now, ct);

        return new Result<SignInResult>(new SignInResult(Token(user, session), refreshToken, session.AbsoluteExpiresOn - now));
    }

    public async Task<Result<SignInResult>> RefreshAsync(SignInContext context, CancellationToken ct = default)
    {
        var presented = context?.PresentedRefreshToken;
        if (!RefreshTokens.IsWellFormed(presented))
            return Expired();

        var hash = RefreshTokens.Hash(presented);
        var stored = await Query<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
            return Expired();

        var now = UtcNow;
        var session = stored.Session;
        if (!await ClaimAsync(stored.Id.Value, now, ct))
            return await ReuseAsync(stored, session, now, ct);

        if (!session.IsActive(now) || session.User.IsDisabled)
        {
            await RevokeAsync(session, now, "expired", ct);
            return Expired();
        }

        session.Touch(now);
        await UpdateAsync(session, ct);
        var (successor, successorId) = await IssueRefreshTokenAsync(session, now, ct);
        stored.SetSuccessor(successorId, _successors.Protect(successor));
        await UpdateAsync(stored, ct);
        var sessionId = session.Id.Value;
        RegisterPostCommitAction(() => EvictAsync(sessionId));

        return new Result<SignInResult>(new SignInResult(Token(session.User, session), successor, session.AbsoluteExpiresOn - now));
    }

    public async Task<Result<bool>> LogoutAsync(SignInContext context, CancellationToken ct = default)
    {
        var now = UtcNow;
        if (CurrentUser.SessionId is { } sessionId && await GetAsync<UserSession>(sessionId, ct) is { } session)
            await RevokeAsync(session, now, "logout", ct);

        await RevokePresentedAsync(context?.PresentedRefreshToken, now, "logout", ct);
        return new Result<bool>(true);
    }

    #endregion

    #region Account changes

    public async Task<Result<TokenViewModel>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        if (request is null || CurrentUser.Id is not { } userId || CurrentUser.SessionId is not { } sessionId)
            return Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials);

        var now = UtcNow;
        var user = await GetAsync<User>(userId, ct);
        var session = await GetAsync<UserSession>(sessionId, ct);
        if (user is null || user.IsDisabled || user.IsLocked(now) || session is null || session.User.Id != user.Id || !session.IsActive(now))
            return Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials);

        if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.StoredPassword(), out _))
        {
            await RecordFailureAsync(user.Id.Value, now, ct);
            return Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials);
        }

        var problems = passwordPolicy.Validate(request.NewPassword, user.UserName).ToList();
        if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
            problems.Add("Choose a password different from the current one.");
        if (problems.Count > 0)
            return Result.Error<TokenViewModel>(problems);

        user.SetPassword(PasswordHasher.Hash(request.NewPassword), temporary: false);
        await UpdateAsync(user, ct);
        var ended = await RevokeAllAsync(user.Id.Value, now, "password-changed", keep: session.Id.Value, ct);
        logger.LogInformation(PasswordChanged, "Account {UserId} changed its password; {Count} other session(s) ended", user.Id, ended);

        return new Result<TokenViewModel>(Token(user, session));
    }

    public async Task<Result<bool>> UnlockAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await GetAsync<User>(userId, ct);
        if (user is null)
            return Result.Error<bool>(ISvcAuthenticator.UserNotFound);

        user.Unlock();
        await UpdateAsync(user, ct);
        logger.LogInformation(AccountUnlocked, "Account {UserId} unlocked by {AdministratorId}", user.Id, CurrentUser.Id);
        return new Result<bool>(true);
    }

    public async Task<Result<bool>> DisableAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await GetAsync<User>(userId, ct);
        if (user is null)
            return Result.Error<bool>(ISvcAuthenticator.UserNotFound);
        if (CurrentUser.Id == userId)
            return Result.Error<bool>(ISvcAuthenticator.CannotChangeOwnAccount);

        user.Disable();
        await UpdateAsync(user, ct);
        var ended = await RevokeAllAsync(userId, UtcNow, "disabled", keep: Guid.Empty, ct);
        RegisterPostCommitAction(() => cache.RemoveByTagAsync(StoredPermissionResolver.Tag(userId)).AsTask());
        logger.LogWarning(AccountDisabled, "Account {UserId} disabled by {AdministratorId}; {Count} session(s) ended", userId, CurrentUser.Id, ended);
        return new Result<bool>(true);
    }

    public async Task<Result<bool>> EnableAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await GetAsync<User>(userId, ct);
        if (user is null)
            return Result.Error<bool>(ISvcAuthenticator.UserNotFound);

        user.Enable();
        await UpdateAsync(user, ct);
        RegisterPostCommitAction(() => cache.RemoveByTagAsync(StoredPermissionResolver.Tag(userId)).AsTask());
        logger.LogInformation(AccountEnabled, "Account {UserId} enabled by {AdministratorId}", userId, CurrentUser.Id);
        return new Result<bool>(true);
    }

    #endregion

    #region Sessions and refresh tokens

    /// <summary>Operational roles get the long idle time; administrators and accounts without an operational role the short one.</summary>
    private TimeSpan IdleTimeout(User user)
    {
        var administrator = user.Roles.Any(r => r.RoleCode == RoleCodes.SystemAdministrator);
        var operational = user.Roles.Any(r => r.RoleCode != RoleCodes.SystemAdministrator);
        return TimeSpan.FromSeconds(!administrator && operational ? settings.Sessions.IdleSecondsOperational : settings.Sessions.IdleSecondsAdministrator);
    }

    private async Task<(string Token, Guid Id)> IssueRefreshTokenAsync(UserSession session, DateTime now, CancellationToken ct)
    {
        var value = RefreshTokens.New();
        var token = await SaveAsync(new RefreshToken(session, RefreshTokens.Hash(value), now), ct);
        return (value, token.Id.Value);
    }

    /// <summary>
    /// Marks a refresh token used in one statement. A second request with the same token waits on the row lock until
    /// the first commits, then finds it used and takes the grace path with the successor the first one stored.
    /// </summary>
    private async Task<bool> ClaimAsync(Guid tokenId, DateTime now, CancellationToken ct)
    {
        var rows = await ExecuteCommandAsync<IdRow>(
            """
            UPDATE refresh_token SET used_on = :now
             WHERE id = :id AND used_on IS NULL AND expires_on > :now
            RETURNING id AS "Id"
            """,
            new Dictionary<string, object> { ["now"] = now, ["id"] = tokenId },
            ct);
        if (rows.Count == 0)
            return false;

        // The token this one replaced no longer needs its successor kept: this one is it, and it is now used.
        await ExecuteCommandAsync<IdRow>(
            """
            UPDATE refresh_token SET successor_protected = NULL
             WHERE successor_id = :id AND successor_protected IS NOT NULL
            RETURNING id AS "Id"
            """,
            new Dictionary<string, object> { ["id"] = tokenId },
            ct);
        return true;
    }

    /// <summary>
    /// A used or expired token was presented. Within the grace window a used token's successor is returned exactly
    /// once (taking it is one statement); any other reuse is treated as theft and the whole family is revoked.
    /// </summary>
    private async Task<Result<SignInResult>> ReuseAsync(RefreshToken stored, UserSession session, DateTime now, CancellationToken ct)
    {
        var state = (await ExecuteSqlAsync<TokenStateRow>(
            """SELECT used_on AS "UsedOn" FROM refresh_token WHERE id = :id""",
            new Dictionary<string, object> { ["id"] = stored.Id.Value },
            ct)).Single();

        var withinGrace = state.UsedOn is { } usedOn && now - usedOn <= TimeSpan.FromSeconds(settings.Sessions.RefreshGraceSeconds);
        if (withinGrace && session.IsActive(now) && !session.User.IsDisabled)
        {
            // RETURNING sees the new row, so the value taken comes from the locked old row.
            var taken = await ExecuteCommandAsync<SuccessorRow>(
                """
                UPDATE refresh_token AS t SET successor_protected = NULL
                  FROM (SELECT id, successor_protected FROM refresh_token
                         WHERE id = :id AND successor_protected IS NOT NULL FOR UPDATE) AS old
                 WHERE t.id = old.id
                RETURNING old.successor_protected AS "Protected"
                """,
                new Dictionary<string, object> { ["id"] = stored.Id.Value },
                ct);
            if (taken.Count == 1)
            {
                var successor = _successors.Unprotect(taken[0].Protected);
                return new Result<SignInResult>(new SignInResult(Token(session.User, session), successor, session.AbsoluteExpiresOn - now));
            }
        }

        if (state.UsedOn is not null && !session.IsRevoked)
        {
            await RevokeAsync(session, now, "refresh-token-reused", ct);
            logger.LogWarning(RefreshTokenReused, "Refresh token reused for session {SessionId} of account {UserId}; the session is revoked",
                session.Id, session.User.Id);
        }

        return Expired();
    }

    private async Task RevokePresentedAsync(string presented, DateTime now, string reason, CancellationToken ct)
    {
        if (!RefreshTokens.IsWellFormed(presented))
            return;

        var hash = RefreshTokens.Hash(presented);
        var token = await Query<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null)
            await RevokeAsync(token.Session, now, reason, ct);
    }

    private async Task RevokeAsync(UserSession session, DateTime now, string reason, CancellationToken ct)
    {
        if (session.IsRevoked)
            return;

        session.Revoke(now, reason);
        await UpdateAsync(session, ct);
        var sessionId = session.Id.Value;
        RegisterPostCommitAction(() => EvictAsync(sessionId));
    }

    /// <summary>Revokes every active session of the user except <paramref name="keep"/> (Guid.Empty keeps none); returns how many.</summary>
    private async Task<int> RevokeAllAsync(Guid userId, DateTime now, string reason, Guid keep, CancellationToken ct)
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
            RegisterPostCommitAction(() => EvictAsync(sessionId));
        }

        if (rows.Count > 0)
            logger.LogInformation(SessionsRevoked, "{Count} session(s) of account {UserId} revoked ({Reason})", rows.Count, userId, reason);
        return rows.Count;
    }

    private Task EvictAsync(Guid sessionId) => cache.RemoveAsync(SessionValidator.Key(sessionId)).AsTask();

    #endregion

    #region Lockout

    /// <summary>
    /// Counts a failed attempt in one UPDATE, so parallel attempts from many addresses cannot lose counts (a read,
    /// increment and write through the session would). The row lock the UPDATE takes serialises concurrent failures
    /// for one account; a locked account is not counted. At the threshold the count resets and the lock is set.
    /// </summary>
    private async Task RecordFailureAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var lockedUntil = now.AddSeconds(settings.Lockout.DurationSeconds);
        var rows = await ExecuteCommandAsync<LockoutRow>(
            """
            UPDATE "user"
               SET failed_login_count = CASE WHEN failed_login_count + 1 >= :threshold THEN 0 ELSE failed_login_count + 1 END,
                   locked_until = CASE WHEN failed_login_count + 1 >= :threshold THEN :lockedUntil ELSE locked_until END
             WHERE id = :id AND (locked_until IS NULL OR locked_until <= :now)
            RETURNING locked_until AS "LockedUntil"
            """,
            new Dictionary<string, object>
            {
                ["threshold"] = settings.Lockout.Threshold,
                ["lockedUntil"] = lockedUntil,
                ["now"] = now,
                ["id"] = userId
            },
            ct);

        if (rows.Count == 1 && rows[0].LockedUntil == lockedUntil)
        {
            logger.LogWarning(AccountLocked, "Account {UserId} locked until {LockedUntil} after {Threshold} failed sign-ins",
                userId, lockedUntil, settings.Lockout.Threshold);
        }
    }

    #endregion

    #region Helpers

    /// <summary>An access token for the session: its sid and family, the sign-in time and methods (auth_time, amr).</summary>
    private TokenViewModel Token(User user, UserSession session)
    {
        var pending = user.IsPending(settings.TotpRequired);
        var token = issuer.Issue(user.Id.Value, user.UserName, session.Id.Value, session.FamilyId, session.AuthenticatedOn, session.Methods(), pending);
        return new TokenViewModel(token, "Bearer", issuer.LifetimeSeconds, pending ? ArivaClaims.PendingScope : null);
    }

    private static Result<SignInResult> Failed() => Result.Error<SignInResult>(ISvcAuthenticator.InvalidCredentials);

    private static Result<SignInResult> Expired() => Result.Error<SignInResult>(ISvcAuthenticator.SessionExpired);

    private sealed class LockoutRow
    {
        public DateTime? LockedUntil { get; set; }
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    private sealed class TokenStateRow
    {
        public DateTime? UsedOn { get; set; }
    }

    private sealed class SuccessorRow
    {
        public string Protected { get; set; }
    }

    #endregion
}
