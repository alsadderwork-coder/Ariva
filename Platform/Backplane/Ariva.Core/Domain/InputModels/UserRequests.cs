using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// A new account (ARV-011). The server generates a temporary password, shown once; the user changes it at first sign-in.
/// Its sites come in the same request (ARV-059): an account is never left without sites where a site-limited
/// administrator could take it over, and a site-limited administrator must give it at least one of its own sites.
/// </summary>
public sealed record CreateUserRequest(
    [Required, MaxLength(64)] string UserName,
    [MaxLength(200)] string DisplayName = null,
    [MaxLength(320), EmailAddress] string Email = null,
    [MaxLength(4)] IReadOnlyList<string> Roles = null,
    bool AllSites = false,
    [MaxLength(64)] IReadOnlyList<string> SiteCodes = null);

/// <summary>Profile fields an administrator can change; roles, password and TOTP have their own audited actions.</summary>
public sealed record UpdateUserRequest(
    [MaxLength(200)] string DisplayName = null,
    [MaxLength(320), EmailAddress] string Email = null);

/// <summary>The sites a user may access (ARV-012): every site, or the listed codes. Replaces the current access.</summary>
public sealed record SiteAccessRequest(
    bool AllSites = false,
    [MaxLength(64)] IReadOnlyList<string> SiteCodes = null);

/// <summary>A new site (ARV-012).</summary>
public sealed record CreateSiteRequest(
    [Required, RegularExpression("^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$"), MaxLength(17)] string Code,
    [Required, MaxLength(200)] string Name);

/// <summary>A site's display name; the code never changes.</summary>
public sealed record UpdateSiteRequest([Required, MaxLength(200)] string Name);
