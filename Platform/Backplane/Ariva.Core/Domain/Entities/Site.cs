using System.Text.RegularExpressions;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A site (glossary): a terminal or group of terminals of an airport deployment with its own configuration, and the
/// unit of data access (ARV-012). Users and integration clients are bound to site codes; every site-bound record
/// carries its site code. Codes are upper case, 2 to 17 characters (for example AMM, AUH-T1), and never change.
/// </summary>
public partial class Site : BaseAuditableEntity<Site>
{
    protected Site()
    {
    }

    public Site(string code, string name)
    {
        if (!IsValidCode(code))
            throw new ArgumentException("A site code is 2 to 8 upper case letters or digits, optionally followed by a hyphen and 1 to 8 more.", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code;
        Name = name.Trim();
    }

    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }

    public virtual void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public static bool IsValidCode(string code) => code is not null && CodePattern().IsMatch(code);

    [GeneratedRegex("^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CodePattern();
}

/// <summary>A site a user may access (ARV-012). A user with <see cref="User.AllSites"/> needs no rows.</summary>
public class UserSite : EntityBase<UserSite>
{
    protected UserSite()
    {
    }

    public UserSite(User user, string siteCode, Guid? grantedById, DateTime? grantedOn)
    {
        User = user;
        SiteCode = siteCode;
        GrantedById = grantedById;
        GrantedOn = grantedOn;
    }

    public virtual User User { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual Guid? GrantedById { get; protected set; }
    public virtual DateTime? GrantedOn { get; protected set; }
}
