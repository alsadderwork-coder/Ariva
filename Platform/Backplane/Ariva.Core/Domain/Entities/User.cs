using Ariva.Core.Security;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A local Ariva account (ADR-0026, ARV-010a). Usernames are stored normalised (<see cref="UserNames"/>); the password
/// is stored only as a PBKDF2 hash with its salt, algorithm and iteration count, so the cost can be raised later and
/// old hashes upgraded at the next sign-in. Brute force state lives on the row: consecutive failures and a lock that
/// expires on its own. Failures are counted by SvcAuthenticator in one SQL statement so concurrent attempts cannot
/// lose counts.
/// </summary>
public class User : BaseAuditableEntity<User>
{
    protected User()
    {
    }

    public User(string userName, string displayName, string email)
    {
        UserName = UserNames.Normalize(userName);
        DisplayName = displayName;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    #region Properties

    public virtual string UserName { get; protected set; }
    public virtual string DisplayName { get; protected set; }
    public virtual string Email { get; protected set; }

    public virtual string PasswordHash { get; protected set; }
    public virtual string PasswordSalt { get; protected set; }
    public virtual string PasswordAlgorithm { get; protected set; }
    public virtual int PasswordIterations { get; protected set; }

    /// <summary>Set for an administrator-issued temporary password; the account is limited to the pending scope.</summary>
    public virtual bool MustChangePassword { get; protected set; }

    /// <summary>Set by TOTP enrolment (ARV-010c).</summary>
    public virtual bool TotpEnrolled { get; protected set; }

    public virtual bool IsDisabled { get; protected set; }
    public virtual int FailedLoginCount { get; protected set; }
    public virtual DateTime? LockedUntil { get; protected set; }
    public virtual DateTime? LastLoginOn { get; protected set; }

    public virtual IList<UserRole> Roles { get; protected set; } = [];

    #endregion

    #region Behaviour

    public virtual bool IsLocked(DateTime utcNow) => LockedUntil is { } until && until > utcNow;

    /// <summary>A temporary password, an unfinished TOTP enrolment (when required) or both: only the pending scope.</summary>
    public virtual bool IsPending(bool totpRequired) => MustChangePassword || (totpRequired && !TotpEnrolled);

    public virtual void SetPassword(PasswordHashValue hash, bool temporary)
    {
        ArgumentNullException.ThrowIfNull(hash);
        PasswordHash = hash.Hash;
        PasswordSalt = hash.Salt;
        PasswordAlgorithm = hash.Algorithm;
        PasswordIterations = hash.Iterations;
        MustChangePassword = temporary;
    }

    public virtual PasswordHashValue StoredPassword() =>
        PasswordHash is null ? null : new PasswordHashValue(PasswordAlgorithm, PasswordIterations, PasswordSalt, PasswordHash);

    public virtual void RecordSuccessfulLogin(DateTime utcNow)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginOn = utcNow;
    }

    public virtual void Unlock()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    public virtual void Disable() => IsDisabled = true;

    public virtual void Enable() => IsDisabled = false;

    public virtual void MarkTotpEnrolled() => TotpEnrolled = true;

    public virtual void Grant(string roleCode)
    {
        if (!RoleCodes.All.Any(code => string.Equals(code, roleCode, StringComparison.Ordinal)))
            throw new ArgumentOutOfRangeException(nameof(roleCode), roleCode, "Unknown role code.");
        if (Roles.Any(r => r.RoleCode == roleCode))
            return;
        Roles.Add(new UserRole(this, roleCode));
    }

    #endregion
}

/// <summary>A role held by a user. ARV-011 adds who granted it and when, through the audited grant service.</summary>
public class UserRole : EntityBase<UserRole>
{
    protected UserRole()
    {
    }

    public UserRole(User user, string roleCode)
    {
        User = user;
        RoleCode = roleCode;
    }

    public virtual User User { get; protected set; }
    public virtual string RoleCode { get; protected set; }
}
