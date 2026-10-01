using System.Data;
using System.Security.Cryptography;
using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Infra.Settings;
using NHibernate.Linq;

namespace Ariva.Infra.Security;

/// <summary>The printed credential of the break-glass account: shown once, to be sealed and stored offline.</summary>
public sealed record BreakGlassCredential(string UserName, string Password, IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// The deployment's emergency account (ADR-0026, ARV-010c). One per deployment (a unique index enforces it), a
/// SystemAdministrator with access to every site (ARV-012) that signs in with its password and one recovery code each time, is never locked out, and
/// raises a critical security event at every sign-in. Only the installer command creates it or rotates its
/// credential; no API can create, enable or reset it.
/// </summary>
internal sealed class BreakGlassAccounts(IUnitOfWork unitOfWork, AuthSettings settings, TimeProvider timeProvider, ILogger<BreakGlassAccounts> logger)
{
    public static readonly EventId BreakGlassIssued = new(9112, "SecurityEvent.BreakGlassCredentialIssued");

    /// <summary>Creates the account, or with <paramref name="rotate"/> gives the existing one a new password and new codes.</summary>
    public async Task<BreakGlassCredential> IssueAsync(bool rotate, CancellationToken ct = default)
    {
        var storage = unitOfWork.StorageProvider;
        storage.BeginTransaction(IsolationLevel.ReadCommitted);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var user = await storage.Query<User>().FirstOrDefaultAsync(u => u.IsBreakGlass, ct);
        if (user is not null && !rotate)
            throw new InvalidOperationException("A break-glass account already exists; use --rotate-break-glass to issue a new credential.");
        if (user is null && rotate)
            throw new InvalidOperationException("There is no break-glass account to rotate; use --create-break-glass.");

        var password = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        if (user is null)
        {
            user = new User(settings.Totp.BreakGlassUserName, "Break-glass administrator", null);
            user.MarkBreakGlass();
            user.SetPassword(PasswordHasher.Hash(password), temporary: false);
            await storage.SaveAsync(user, ct);
            user.Grant(RoleCodes.SystemAdministrator);
            await storage.SaveAsync(user.Roles.Last(), ct);
            user.SetSites(true, [], null, now);
            await storage.UpdateAsync(user, ct);
        }
        else
        {
            user.SetPassword(PasswordHasher.Hash(password), temporary: false);
            user.Enable();
            user.Unlock();
            user.SetSites(true, user.Sites.Select(s => s.SiteCode).ToList(), null, now);
            await storage.UpdateAsync(user, ct);
            await storage.ExecuteSqlAsync<Row>(
                """UPDATE user_session SET revoked_on = :now, revoked_reason = 'break-glass-rotated' WHERE user_id = :id AND revoked_on IS NULL RETURNING id AS "Id" """,
                new Dictionary<string, object> { ["now"] = now, ["id"] = user.Id.Value }, ct);
            await storage.ExecuteSqlAsync<Row>(
                """UPDATE recovery_code SET used_on = :now WHERE user_id = :id AND used_on IS NULL RETURNING id AS "Id" """,
                new Dictionary<string, object> { ["now"] = now, ["id"] = user.Id.Value }, ct);
        }

        var codes = new List<string>(RecoveryCodes.Count);
        while (codes.Count < RecoveryCodes.Count)
        {
            var code = RecoveryCodes.New();
            if (codes.Any(existing => existing == code))
                continue;
            codes.Add(code);
            await storage.SaveAsync(new RecoveryCode(user, RecoveryCodes.Hash(RecoveryCodes.Normalize(code)), now), ct);
        }

        unitOfWork.PromiseToCommit();
        await unitOfWork.EndAsync(ct);
        logger.LogCritical(BreakGlassIssued, "Break-glass credential {Action} for account {UserId}", rotate ? "rotated" : "created", user.Id);
        return new BreakGlassCredential(user.UserName, password, codes);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
    }
}
