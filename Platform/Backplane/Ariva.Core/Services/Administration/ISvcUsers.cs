using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Administration;

/// <summary>Errors the administration services return; the controllers map them to status codes.</summary>
public static class AdministrationErrors
{
    public const string NotFound = "The record does not exist.";
    public const string UserNameTaken = "That username is already in use.";
    public const string InvalidUserName = "The username must be 3 to 64 letters, digits or the characters . _ - @.";
    public const string UnknownRole = "Unknown role.";
    public const string OwnAccount = "You cannot change your own roles, password or authenticator here.";
    public const string AboveOwnRole = "You cannot grant or revoke a role above your own.";
    public const string LastAdministrator = "The last active system administrator keeps the role.";
    public const string InvalidCriteria = "The search criteria are not valid.";
    public const string UnknownSite = "Unknown site.";
    public const string SiteTaken = "That site code is already in use.";
    public const string BeyondOwnSites = "You cannot grant access to sites you cannot access yourself.";

    /// <summary>Errors that mean "not allowed" (403) rather than "not valid" (400).</summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.Ordinal) { OwnAccount, AboveOwnRole, BeyondOwnSites };
}

/// <summary>Account administration (ARV-011): every change is audited in the same transaction.</summary>
public interface ISvcUsers : ISvcScoped
{
    Task<Result<UserCreatedViewModel>> CreateAsync(CreateUserRequest request, CancellationToken ct = default);

    Task<Result<UserViewModel>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<UserViewModel>>> SearchAsync(UserCriteria criteria, CancellationToken ct = default);

    Task<Result<UserViewModel>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);

    /// <summary>A new temporary password, shown once; every session of the user ends.</summary>
    Task<Result<TemporaryPasswordViewModel>> ResetPasswordAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Replaces the user's site access (ARV-012); only within the caller's own access, never on the caller's own
    /// account. Audited; the user's cached access is evicted after commit.
    /// </summary>
    Task<Result<UserViewModel>> SetSitesAsync(Guid id, SiteAccessRequest request, CancellationToken ct = default);

    /// <summary>Clears the authenticator and recovery codes; the user enrols again at the next sign-in and every session ends.</summary>
    Task<Result<bool>> ResetTotpAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Role grants (ARV-011, CWE-269): no self-grant, nothing above the granter's own rank, audited.</summary>
public interface ISvcRoleAssignment : ISvcScoped
{
    IReadOnlyList<RoleViewModel> Roles();

    Task<Result<UserViewModel>> GrantAsync(Guid userId, string roleCode, CancellationToken ct = default);

    /// <summary>Revokes the role and ends the user's sessions; the last active SystemAdministrator keeps the role.</summary>
    Task<Result<UserViewModel>> RevokeAsync(Guid userId, string roleCode, CancellationToken ct = default);
}

/// <summary>The audit trail, read-only (ARV-011).</summary>
public interface ISvcAuditEntries : ISvcScoped
{
    Task<Result<AuditEntryViewModel>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<AuditEntryViewModel>>> SearchAsync(AuditEntryCriteria criteria, CancellationToken ct = default);
}

/// <summary>Sites (ARV-012): everyone reads only the sites they are bound to; administrators create and rename them.</summary>
public interface ISvcSites : ISvcScoped
{
    Task<Result<IReadOnlyList<SiteViewModel>>> ListAsync(CancellationToken ct = default);

    /// <summary>The site, or NotFound when it does not exist or is outside the caller's sites (no difference shown).</summary>
    Task<Result<SiteViewModel>> GetAsync(string code, CancellationToken ct = default);

    Task<Result<SiteViewModel>> CreateAsync(CreateSiteRequest request, CancellationToken ct = default);

    Task<Result<SiteViewModel>> UpdateAsync(string code, UpdateSiteRequest request, CancellationToken ct = default);
}
