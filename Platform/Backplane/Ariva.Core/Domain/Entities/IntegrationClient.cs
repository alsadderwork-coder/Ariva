using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Integration;
using Ariva.Core.Security;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A system that calls the Integration API (ARV-042, docs/architecture/integration.md): an AODB, an immigration system,
/// a sensor gateway. It proves itself with a client id, a client secret (shown once, stored as a PBKDF2-SHA256 hash)
/// and a TOTP code from a seed (shown once, stored encrypted with the Data Protection key ring), from one of its allowed
/// networks. It may call only the endpoints of its scopes, only for its sites. Disabled clients and clients locked after
/// repeated failures get no token; every change of what a client may do (scopes, sites, networks, policy, secret,
/// seed, status) invalidates the tokens it already holds (<see cref="TokensValidFromUtc"/>).
/// </summary>
public partial class IntegrationClient : BaseAuditableEntity<IntegrationClient>
{
    public const int MaxNameLength = 100;
    public const int MaxSites = 32;

    protected IntegrationClient()
    {
    }

    public IntegrationClient(string clientId, string name, IntegrationClientKind kind, IntegrationClientAccess access, PasswordHashValue secretHash, string totpSecretProtected,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(secretHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(totpSecretProtected);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Id = NewId();
        ClientId = IsClientId(clientId) ? clientId : throw new ArgumentException("A client id is ic_ and 26 lower case letters or digits.", nameof(clientId));
        Kind = kind;
        Rename(name);
        Apply(access);
        SetSecret(secretHash, utcNow);
        SetTotp(totpSecretProtected, utcNow);
        Status = IntegrationClientStatus.Active;
    }

    /// <summary>Public: the caller names itself with it. ic_ and 26 lower-case base32 characters (130 random bits).</summary>
    public virtual string ClientId { get; protected set; }

    public virtual string Name { get; protected set; }
    public virtual IntegrationClientKind Kind { get; protected set; }
    public virtual IntegrationClientStatus Status { get; protected set; }

    /// <summary>Scopes, space separated (each one of <see cref="IntegrationScopes.All"/>).</summary>
    public virtual string ScopeNames { get; protected set; }

    /// <summary>Bound site codes, space separated: the only sites the client may write or read.</summary>
    public virtual string SiteCodes { get; protected set; }

    /// <summary>Allowed source networks in CIDR form, space separated; empty for any address.</summary>
    public virtual string AllowedNetworks { get; protected set; }

    /// <summary>Every call carries a fresh TOTP code in X-TOTP-Code (AMAN parity; on by default for immigration clients).</summary>
    public virtual bool RequireTotpPerRequest { get; protected set; }

    public virtual string SecretAlgorithm { get; protected set; }
    public virtual int SecretIterations { get; protected set; }
    public virtual string SecretSalt { get; protected set; }
    public virtual string SecretHash { get; protected set; }
    public virtual DateTime SecretChangedUtc { get; protected set; }

    /// <summary>The TOTP seed, base32, protected with the Data Protection purpose <c>Ariva.Totp.v1</c>.</summary>
    public virtual string TotpSecretProtected { get; protected set; }

    /// <summary>The last time step accepted at a token exchange (replay guard, CWE-294).</summary>
    public virtual long? TotpLastStep { get; protected set; }

    public virtual DateTime TotpChangedUtc { get; protected set; }
    public virtual int FailedAttempts { get; protected set; }
    public virtual DateTime? LockedUntilUtc { get; protected set; }

    /// <summary>When the client last changed in a way that stops its tokens.</summary>
    public virtual DateTime TokensValidFromUtc { get; protected set; }

    /// <summary>Bumped by every such change; a token carries the version it was issued for and is refused for any other.</summary>
    public virtual int TokenVersion { get; protected set; }

    public virtual DateTime? LastTokenUtc { get; protected set; }

    public virtual PasswordHashValue Secret => new(SecretAlgorithm, SecretIterations, SecretSalt, SecretHash);

    public virtual IReadOnlyList<string> Scopes => Split(ScopeNames);
    public virtual IReadOnlyList<string> Sites => Split(SiteCodes);
    public virtual IReadOnlyList<string> Networks => Split(AllowedNetworks);

    public virtual bool IsLocked(DateTime now) => LockedUntilUtc is { } until && until > now;

    public virtual bool HasScope(string scope) => System.Linq.Enumerable.Contains(Scopes, scope, StringComparer.Ordinal);

    public virtual bool Serves(string siteCode) => siteCode is not null && System.Linq.Enumerable.Contains(Sites, siteCode, StringComparer.Ordinal);

    public virtual void Rename(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || !Components.DisplayText.IsClean(trimmed))
            throw new ArgumentException("A name is 1 to 100 characters of plain text.", nameof(name));
        Name = trimmed;
    }

    /// <summary>Sets scopes, sites, networks and policy; true when they changed (and the client's tokens stop working).</summary>
    public virtual bool Apply(IntegrationClientAccess access, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        var before = (ScopeNames, SiteCodes, AllowedNetworks, RequireTotpPerRequest);
        (ScopeNames, SiteCodes, AllowedNetworks, RequireTotpPerRequest) =
            (string.Join(' ', access.Scopes), string.Join(' ', access.Sites), access.Networks.Count == 0 ? null : string.Join(' ', access.Networks), access.RequireTotpPerRequest);
        var changed = before != (ScopeNames, SiteCodes, AllowedNetworks, RequireTotpPerRequest);
        if (changed && utcNow is { } now)
            Invalidate(now);
        return changed;
    }

    public virtual void SetSecret(PasswordHashValue hash, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(hash);
        (SecretAlgorithm, SecretIterations, SecretSalt, SecretHash) = (hash.Algorithm, hash.Iterations, hash.Salt, hash.Hash);
        SecretChangedUtc = utcNow;
        Invalidate(utcNow);
    }

    public virtual void SetTotp(string protectedSecret, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSecret);
        TotpSecretProtected = protectedSecret;
        TotpLastStep = null;
        TotpChangedUtc = utcNow;
        Invalidate(utcNow);
    }

    /// <summary>Stops the client at once (its tokens too); true when it was active.</summary>
    public virtual bool Disable(DateTime utcNow)
    {
        if (Status == IntegrationClientStatus.Disabled)
            return false;
        Status = IntegrationClientStatus.Disabled;
        Invalidate(utcNow);
        return true;
    }

    public virtual bool Enable()
    {
        if (Status == IntegrationClientStatus.Active)
            return false;
        Status = IntegrationClientStatus.Active;
        return true;
    }

    /// <summary>Clears a lockout and the failure count; true when it was locked or had failures.</summary>
    public virtual bool Unlock()
    {
        if (LockedUntilUtc is null && FailedAttempts == 0)
            return false;
        (LockedUntilUtc, FailedAttempts) = (null, 0);
        return true;
    }

    private void Invalidate(DateTime utcNow)
    {
        TokensValidFromUtc = utcNow;
        TokenVersion++;
    }

    public static bool IsClientId(string value) => value is not null && ClientIdShape().IsMatch(value);

    private static string[] Split(string value) => string.IsNullOrEmpty(value) ? [] : value.Split(' ');

    [GeneratedRegex(@"^ic_[a-z2-7]{26}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ClientIdShape();
}

/// <summary>What a client may do, checked: known scopes (at least one), 1 to 32 sites, CIDR networks, the per-request TOTP policy.</summary>
public sealed record IntegrationClientAccess(IReadOnlyList<string> Scopes, IReadOnlyList<string> Sites, IReadOnlyList<string> Networks, bool RequireTotpPerRequest)
{
    /// <summary>The access, normalised (distinct, scopes in their fixed order, sites sorted), or the reasons it cannot be taken.</summary>
    public static (IntegrationClientAccess Access, IReadOnlyList<string> Errors) Check(IEnumerable<string> scopes, IEnumerable<string> sites, IEnumerable<string> networks,
        bool requireTotpPerRequest)
    {
        var errors = new List<string>();
        var scopeList = (scopes ?? []).Where(s => s is not null).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (scopeList.Count == 0 || scopeList.Any(s => !IntegrationScopes.IsKnown(s)))
            errors.Add("Scopes are one or more of " + string.Join(", ", IntegrationScopes.All) + ".");
        var siteList = (sites ?? []).Where(s => s is not null).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (siteList.Count is 0 or > IntegrationClient.MaxSites || siteList.Any(s => !Site.IsValidCode(s)))
            errors.Add("A client is bound to 1 to 32 site codes.");
        var (blocks, networkError) = SourceNetworks.Normalize(networks);
        if (networkError is not null)
            errors.Add(networkError);
        // At least one network: a client reachable from anywhere can be worn down by anyone who learns its public id.
        else if (blocks.Count == 0)
            errors.Add("A client has 1 to 16 allowed source networks in CIDR form.");
        if (errors.Count > 0)
            return (null, errors);
        var ordered = IntegrationScopes.All.Where(scopeList.Contains).ToList();
        return (new IntegrationClientAccess(ordered, siteList, blocks, requireTotpPerRequest), errors);
    }
}
