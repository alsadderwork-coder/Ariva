namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A server-side sign-in session (ADR-0026, ARV-010b). The access token's sid is this row's id; every authenticated
/// request checks that it is active, so logout, a disabled account or a reused refresh token end access at once
/// instead of at token expiry. One session is one refresh token family.
/// </summary>
public class UserSession : EntityBase<UserSession>
{
    public const int UserAgentMaxLength = 512;

    protected UserSession()
    {
    }

    public UserSession(User user, DateTime utcNow, IReadOnlyList<string> methods, TimeSpan idleTimeout, TimeSpan absoluteLifetime, string ipAddress, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(methods);
        User = user;
        FamilyId = NewId();
        StartedOn = utcNow;
        AuthenticatedOn = utcNow;
        AuthenticationMethods = string.Join(' ', methods);
        IdleTimeoutSeconds = (int)idleTimeout.TotalSeconds;
        AbsoluteExpiresOn = utcNow + absoluteLifetime;
        LastSeenOn = utcNow;
        IdleExpiresOn = Earlier(utcNow + idleTimeout, AbsoluteExpiresOn);
        IpAddress = ipAddress;
        UserAgent = userAgent is { Length: > UserAgentMaxLength } ? userAgent[..UserAgentMaxLength] : userAgent;
    }

    #region Properties

    public virtual User User { get; protected set; }

    /// <summary>The refresh token family; every token rotated from this sign-in belongs to it.</summary>
    public virtual Guid FamilyId { get; protected set; }

    public virtual DateTime StartedOn { get; protected set; }

    /// <summary>When the user last proved who they are (auth_time); step-up (ARV-010d) moves it forward.</summary>
    public virtual DateTime AuthenticatedOn { get; protected set; }

    /// <summary>Space separated amr values, for example "pwd" or "pwd otp".</summary>
    public virtual string AuthenticationMethods { get; protected set; }

    public virtual DateTime LastSeenOn { get; protected set; }
    public virtual int IdleTimeoutSeconds { get; protected set; }
    public virtual DateTime IdleExpiresOn { get; protected set; }
    public virtual DateTime AbsoluteExpiresOn { get; protected set; }
    public virtual string IpAddress { get; protected set; }
    public virtual string UserAgent { get; protected set; }
    public virtual DateTime? RevokedOn { get; protected set; }
    public virtual string RevokedReason { get; protected set; }

    #endregion

    #region Behaviour

    public virtual IReadOnlyList<string> Methods() => AuthenticationMethods.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public virtual bool IsRevoked => RevokedOn is not null;

    public virtual bool IsExpired(DateTime utcNow) => utcNow >= IdleExpiresOn || utcNow >= AbsoluteExpiresOn;

    public virtual bool IsActive(DateTime utcNow) => !IsRevoked && !IsExpired(utcNow);

    /// <summary>Activity (a refresh): the idle deadline moves forward, never past the absolute one.</summary>
    public virtual void Touch(DateTime utcNow)
    {
        LastSeenOn = utcNow;
        IdleExpiresOn = Earlier(utcNow.AddSeconds(IdleTimeoutSeconds), AbsoluteExpiresOn);
    }

    /// <summary>Ends the session; the first reason is kept.</summary>
    public virtual void Revoke(DateTime utcNow, string reason)
    {
        if (IsRevoked)
            return;
        RevokedOn = utcNow;
        RevokedReason = reason;
    }

    private static DateTime Earlier(DateTime a, DateTime b) => a <= b ? a : b;

    #endregion
}

/// <summary>
/// One refresh token of a session's family, stored only as its SHA-256 hash (ARV-010b). A token is used once; the
/// successor issued for it is kept encrypted for the grace window, so a second tab refreshing at the same moment gets
/// the same successor instead of revoking the family.
/// </summary>
public class RefreshToken : EntityBase<RefreshToken>
{
    protected RefreshToken()
    {
    }

    public RefreshToken(UserSession session, string tokenHash, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        Session = session;
        TokenHash = tokenHash;
        IssuedOn = utcNow;
        ExpiresOn = session.AbsoluteExpiresOn;
    }

    public virtual UserSession Session { get; protected set; }

    /// <summary>Lower-case hex SHA-256 of the token; the token itself is never stored.</summary>
    public virtual string TokenHash { get; protected set; }

    public virtual DateTime IssuedOn { get; protected set; }
    public virtual DateTime ExpiresOn { get; protected set; }
    public virtual DateTime? UsedOn { get; protected set; }
    public virtual Guid? SuccessorId { get; protected set; }

    /// <summary>The successor token, Data Protection encrypted; returned once within the grace window, then cleared.</summary>
    public virtual string SuccessorProtected { get; protected set; }

    public virtual void SetSuccessor(Guid successorId, string successorProtected)
    {
        SuccessorId = successorId;
        SuccessorProtected = successorProtected;
    }
}
