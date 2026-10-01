using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Security;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Settings;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Security;

/// <summary>
/// Username and password sign-in (ADR-0026, ARV-010a).
/// <list type="bullet">
/// <item>Every failure returns <see cref="ISvcAuthenticator.InvalidCredentials"/> after one full PBKDF2 verification,
/// including unknown users (verified against a dummy hash), disabled and locked accounts (CWE-204, CWE-208).</item>
/// <item>Ten consecutive failures lock the account for 15 minutes (both configurable); the lock expires on its own and
/// is logged as a security event. A locked account's correct password still fails. Failures are counted atomically in
/// the database, so parallel attempts from many addresses cannot get past the threshold.</item>
/// <item>Nothing secret is logged: no arguments, no password, no token (CWE-532).</item>
/// </list>
/// The per-IP limit (10 a minute) is the "auth" rate limiting policy on the endpoint.
/// </summary>
internal sealed class SvcAuthenticator(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    AuthSettings settings,
    PasswordPolicy passwordPolicy,
    AccessTokenIssuer issuer,
    ILogger<SvcAuthenticator> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAuthenticator
{
    public static readonly EventId AccountLocked = new(9101, "SecurityEvent.AccountLocked");
    public static readonly EventId AccountUnlocked = new(9102, "SecurityEvent.AccountUnlocked");
    public static readonly EventId PasswordChanged = new(9103, "SecurityEvent.PasswordChanged");

    // Verified when the username is unknown, so the response takes as long as a real check.
    private static readonly Lazy<PasswordHashValue> DummyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString("N")));

    private static readonly string[] PasswordMethod = ["pwd"];

    public async Task<Result<TokenViewModel>> LoginAsync(LoginRequest request, CancellationToken ct = default)
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

        return Issue(user, now);
    }

    public async Task<Result<TokenViewModel>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        if (request is null || CurrentUser.Id is not { } userId)
            return Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials);

        var user = await GetAsync<User>(userId, ct);
        if (user is null || user.IsDisabled || user.IsLocked(UtcNow))
            return Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials);

        if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.StoredPassword(), out _))
        {
            await RecordFailureAsync(user.Id.Value, UtcNow, ct);
            return Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials);
        }

        var problems = passwordPolicy.Validate(request.NewPassword, user.UserName).ToList();
        if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
            problems.Add("Choose a password different from the current one.");
        if (problems.Count > 0)
            return Result.Error<TokenViewModel>(problems);

        user.SetPassword(PasswordHasher.Hash(request.NewPassword), temporary: false);
        await UpdateAsync(user, ct);
        logger.LogInformation(PasswordChanged, "Account {UserId} changed its password", user.Id);

        return Issue(user, UtcNow);
    }

    public async Task<Result<bool>> UnlockAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await GetAsync<User>(userId, ct);
        if (user is null)
            return Result.Error<bool>("The user does not exist.");

        user.Unlock();
        await UpdateAsync(user, ct);
        logger.LogInformation(AccountUnlocked, "Account {UserId} unlocked by {AdministratorId}", user.Id, CurrentUser.Id);
        return new Result<bool>(true);
    }

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

    private sealed class LockoutRow
    {
        public DateTime? LockedUntil { get; set; }
    }

    private Result<TokenViewModel> Issue(User user, DateTime authenticatedAt)
    {
        var pending = user.IsPending(settings.TotpRequired);
        var token = issuer.Issue(user.Id.Value, user.UserName, Guid.CreateVersion7(), Guid.CreateVersion7(), authenticatedAt, PasswordMethod, pending);
        return new Result<TokenViewModel>(new TokenViewModel(token, "Bearer", issuer.LifetimeSeconds, pending ? ArivaClaims.PendingScope : null));
    }

    private static Result<TokenViewModel> Failed() => Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials);
}
