using Ariva.Core;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Administration;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Security;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Administration;

/// <summary>
/// Account administration (ARV-011). Every change writes an audit entry in the same transaction; a password or TOTP
/// reset ends every session of the user. The break-glass account is invisible here (404): only the installer command
/// manages it. An administrator resets its own password and authenticator through the self-service flows, not here.
/// </summary>
internal sealed class SvcUsers(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    CallerRoles callerRoles,
    ISiteScope siteScope,
    AdministrationGuards guards,
    AccountSessions sessions,
    AuditTrail audit,
    PasswordPolicy passwordPolicy,
    ILogger<SvcUsers> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcUsers
{
    public static readonly EventId UserCreated = new(9120, "SecurityEvent.UserCreated");
    public static readonly EventId PasswordReset = new(9121, "SecurityEvent.PasswordReset");
    public static readonly EventId TotpReset = new(9122, "SecurityEvent.TotpReset");
    public static readonly EventId SitesChanged = new(9125, "SecurityEvent.SitesChanged");

    public async Task<Result<UserCreatedViewModel>> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        if (request is null || !UserNames.IsValid(request.UserName))
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.InvalidUserName);

        var roles = (request.Roles ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (roles.Any(role => RoleHierarchy.Rank(role) == 0))
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.UnknownRole);
        var granter = await callerRoles.GetAsync(ct);
        if (roles.Any(role => !RoleHierarchy.CanAssign(granter, role)))
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.AboveOwnRole);

        if (!IsUsableEmail(request.Email))
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.InvalidEmail);
        if (!IsUsableDisplayName(request.DisplayName))
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.InvalidDisplayName);

        // The sites in the same request: a site-limited administrator gives at least one of its own (a sites-less
        // account is administrable by all-sites administrators only, so it would be beyond its creator at once).
        var siteCodes = (request.SiteCodes ?? []).Distinct(StringComparer.Ordinal).ToList();
        var sitesProblem = await SitesProblemAsync(request.AllSites, siteCodes, ct) ?? ObserverSitesProblem(request.AllSites, roles);
        if (sitesProblem is not null)
            return Result.Error<UserCreatedViewModel>(sitesProblem);
        if (!request.AllSites && siteCodes.Count == 0 && !(await siteScope.GetAsync(ct)).AllSites)
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.SitesRequired);

        var userName = UserNames.Normalize(request.UserName);
        if (await Query<User>().AnyAsync(u => u.UserName == userName, ct))
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.UserNameTaken);

        var now = UtcNow;
        var password = TemporaryPasswords.New(passwordPolicy, userName);
        var user = new User(userName, request.DisplayName?.Trim(), request.Email);
        user.SetPassword(PasswordHasher.Hash(password), temporary: true);
        await SaveAsync(user, ct);
        foreach (var role in roles)
            await SaveAsync(user.Grant(role, CurrentUser.Id, now), ct);
        foreach (var site in user.SetSites(request.AllSites, siteCodes, CurrentUser.Id, now).Added)
            await SaveAsync(site, ct);
        if (request.AllSites)
            await UpdateAsync(user, ct);

        await audit.RecordAsync(AuditActions.UserCreated, AuditActions.UserTarget, user.Id, user.UserName, null, AuditTrail.Summary(user), ct);
        logger.LogInformation(UserCreated, "Account {UserId} created by {AdministratorId} with roles {Roles}", user.Id, CurrentUser.Id, string.Join(",", roles));
        return new Result<UserCreatedViewModel>(new UserCreatedViewModel(View(user, now), password));
    }

    // ARV-040: an address alert emails can go to, or none (CWE-93: nothing that could add a header or a recipient).
    /// <summary>
    /// Why the caller cannot give this site access, or null. Scope first: a site outside the caller's own answers the
    /// same whether it exists or not (CWE-204); then every code must be a site.
    /// </summary>
    private async Task<string> SitesProblemAsync(bool allSites, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var wanted = new SiteAccess(allSites, codes.ToHashSet(StringComparer.Ordinal));
        if (!(await siteScope.GetAsync(ct)).Covers(wanted))
            return AdministrationErrors.BeyondOwnSites;
        if (codes.Any(code => !Site.IsValidCode(code)))
            return AdministrationErrors.UnknownSite;
        if (codes.Count > 0 && await Query<Site>().CountAsync(s => codes.Contains(s.Code), ct) != codes.Count)
            return AdministrationErrors.UnknownSite;
        return null;
    }

    /// <summary>
    /// The site access check of the role (ARV-104a, first security review, 2026-10-08): an account whose only role is Validation
    /// observer is bound to named sites and never given every site (<see cref="RoleCodes.NeedsNamedSites"/>). Checked after
    /// the caller's own scope, so a site-limited administrator still gets its 403 first. Role grants and revokes apply the
    /// same rule (<see cref="SvcRoleAssignment"/>).
    /// </summary>
    internal static string ObserverSitesProblem(bool allSites, IEnumerable<string> roles) =>
        allSites && RoleCodes.NeedsNamedSites(roles) ? AdministrationErrors.ObserverNeedsNamedSites : null;

    /// <summary>
    /// A name is shown on screens and in the audit trail: no control, invisible or broken characters (CWE-117, CWE-79),
    /// except the zero-width non-joiner and joiner, which Persian and some Indic names need.
    /// </summary>
    internal static bool IsUsableDisplayName(string displayName) =>
        string.IsNullOrWhiteSpace(displayName) ||
        (displayName.Trim().Length <= 200 && DisplayText.IsClean(displayName.Trim().Replace("\u200C", "", StringComparison.Ordinal).Replace("\u200D", "", StringComparison.Ordinal)));

    private static bool IsUsableEmail(string email) => string.IsNullOrWhiteSpace(email) || Ariva.Infra.Notifications.EmailAddresses.IsValid(email.Trim());

    public async Task<Result<UserViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var user = await VisibleAsync(id, ct);
        return user is null ? Result.Error<UserViewModel>(AdministrationErrors.NotFound) : new Result<UserViewModel>(View(user, UtcNow));
    }

    public async Task<Result<PageViewModel<UserViewModel>>> SearchAsync(UserCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new UserCriteria();
        if (criteria.SortBy is not null && !UserCriteria.SortFields.Any(field => string.Equals(field, criteria.SortBy, StringComparison.OrdinalIgnoreCase)))
            return Result.Error<PageViewModel<UserViewModel>>(AdministrationErrors.InvalidCriteria);
        if (criteria.Role is not null && RoleHierarchy.Rank(criteria.Role) == 0)
            return Result.Error<PageViewModel<UserViewModel>>(AdministrationErrors.UnknownRole);

        var query = AdministrationGuards.Administrable(QueryAsNoTracking<User>().Where(u => !u.IsBreakGlass), await siteScope.GetAsync(ct));
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.Trim().ToLowerInvariant();
            query = query.Where(u => u.UserName.Contains(text) || (u.DisplayName != null && u.DisplayName.ToLower().Contains(text)));
        }

        if (criteria.Role is { } role)
            query = query.Where(u => u.Roles.Any(r => r.RoleCode == role));
        if (criteria.IsDisabled is { } disabled)
            query = query.Where(u => u.IsDisabled == disabled);

        var ordered = (criteria.SortBy?.ToLowerInvariant(), criteria.SortDescending) switch
        {
            ("displayname", false) => query.OrderBy(u => u.DisplayName).ThenBy(u => u.UserName),
            ("displayname", true) => query.OrderByDescending(u => u.DisplayName).ThenBy(u => u.UserName),
            ("lastloginon", false) => query.OrderBy(u => u.LastLoginOn).ThenBy(u => u.UserName),
            ("lastloginon", true) => query.OrderByDescending(u => u.LastLoginOn).ThenBy(u => u.UserName),
            ("createdon", false) => query.OrderBy(u => u.CreatedOn).ThenBy(u => u.UserName),
            ("createdon", true) => query.OrderByDescending(u => u.CreatedOn).ThenBy(u => u.UserName),
            (_, true) => query.OrderByDescending(u => u.UserName),
            _ => query.OrderBy(u => u.UserName)
        };

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await query.CountAsync(ct);
        var users = await ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Roles in a second query: fetching a collection together with paging would page the joined rows.
        var ids = users.Select(u => u.Id).ToList();
        var grants = (await QueryAsNoTracking<UserRole>().Where(r => ids.Contains(r.User.Id)).Select(r => new { UserId = r.User.Id, r.RoleCode }).ToListAsync(ct))
            .ToLookup(g => g.UserId, g => g.RoleCode);
        var bindings = (await QueryAsNoTracking<UserSite>().Where(b => ids.Contains(b.User.Id)).Select(b => new { UserId = b.User.Id, b.SiteCode }).ToListAsync(ct))
            .ToLookup(b => b.UserId, b => b.SiteCode);
        var now = UtcNow;
        var data = users.Select(u => View(u, now, grants[u.Id], bindings[u.Id])).ToList();
        return new Result<PageViewModel<UserViewModel>>(new PageViewModel<UserViewModel>(data, total, pageIndex, pageSize));
    }

    public async Task<Result<UserViewModel>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await VisibleAsync(id, ct);
        if (user is null)
            return Result.Error<UserViewModel>(AdministrationErrors.NotFound);

        if (!IsUsableEmail(request?.Email))
            return Result.Error<UserViewModel>(AdministrationErrors.InvalidEmail);
        if (!IsUsableDisplayName(request?.DisplayName))
            return Result.Error<UserViewModel>(AdministrationErrors.InvalidDisplayName);

        var before = AuditTrail.Summary(user);
        user.UpdateProfile(request?.DisplayName, request?.Email);
        await UpdateAsync(user, ct);
        await audit.RecordAsync(AuditActions.UserUpdated, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        return new Result<UserViewModel>(View(user, UtcNow));
    }

    public async Task<Result<TemporaryPasswordViewModel>> ResetPasswordAsync(Guid id, CancellationToken ct = default)
    {
        if (id == CurrentUser.Id)
            return Result.Error<TemporaryPasswordViewModel>(AdministrationErrors.OwnAccount);
        var user = await VisibleAsync(id, ct);
        if (user is null)
            return Result.Error<TemporaryPasswordViewModel>(AdministrationErrors.NotFound);

        var before = AuditTrail.Summary(user);
        var password = TemporaryPasswords.New(passwordPolicy, user.UserName);
        user.SetPassword(PasswordHasher.Hash(password), temporary: true);
        user.Unlock();
        await UpdateAsync(user, ct);
        var ended = await sessions.RevokeAllAsync(user.Id.Value, UtcNow, "password-reset", Guid.Empty, ct);
        await audit.RecordAsync(AuditActions.PasswordReset, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        logger.LogWarning(PasswordReset, "Password of account {UserId} reset by {AdministratorId}; {Count} session(s) ended", user.Id, CurrentUser.Id, ended);
        return new Result<TemporaryPasswordViewModel>(new TemporaryPasswordViewModel(password));
    }

    public async Task<Result<UserViewModel>> SetSitesAsync(Guid id, SiteAccessRequest request, CancellationToken ct = default)
    {
        if (id == CurrentUser.Id)
            return Result.Error<UserViewModel>(AdministrationErrors.OwnAccount);
        var user = await VisibleAsync(id, ct);
        if (user is null)
            return Result.Error<UserViewModel>(AdministrationErrors.NotFound);

        var codes = (request?.SiteCodes ?? []).Distinct(StringComparer.Ordinal).ToList();
        var allSites = request?.AllSites == true;

        var sitesProblem = await SitesProblemAsync(allSites, codes, ct) ?? ObserverSitesProblem(allSites, user.Roles.Select(r => r.RoleCode));
        if (sitesProblem is not null)
            return Result.Error<UserViewModel>(sitesProblem);

        var before = AuditTrail.Summary(user);
        var narrowed = (user.AllSites && !allSites) || user.Sites.Any(s => !codes.Contains(s.SiteCode));
        var (removed, added) = user.SetSites(allSites, codes, CurrentUser.Id, UtcNow);
        foreach (var site in removed)
            await DeleteAsync(site, ct);
        foreach (var site in added)
            await SaveAsync(site, ct);
        await UpdateAsync(user, ct);
        sessions.EvictPermissionsAfterCommit(user.Id.Value);
        var ended = narrowed ? await sessions.RevokeAllAsync(user.Id.Value, UtcNow, "sites-narrowed", Guid.Empty, ct) : 0;
        await audit.RecordAsync(AuditActions.SitesChanged, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        logger.LogWarning(SitesChanged, "Site access of account {UserId} set by {AdministratorId}: all {AllSites}, {Sites}; {Count} session(s) ended",
            user.Id, CurrentUser.Id, allSites, string.Join(",", codes), ended);
        return new Result<UserViewModel>(View(user, UtcNow));
    }

    public async Task<Result<bool>> ResetTotpAsync(Guid id, CancellationToken ct = default)
    {
        if (id == CurrentUser.Id)
            return Result.Error<bool>(AdministrationErrors.OwnAccount);
        var user = await VisibleAsync(id, ct);
        if (user is null)
            return Result.Error<bool>(AdministrationErrors.NotFound);

        var now = UtcNow;
        var before = AuditTrail.Summary(user);
        user.ResetTotp();
        await UpdateAsync(user, ct);
        await ExecuteCommandAsync<IdRow>(
            """
            UPDATE recovery_code SET used_on = :now
             WHERE user_id = :userId AND used_on IS NULL
            RETURNING id AS "Id"
            """,
            new Dictionary<string, object> { ["now"] = now, ["userId"] = user.Id.Value },
            ct);
        var ended = await sessions.RevokeAllAsync(user.Id.Value, now, "totp-reset", Guid.Empty, ct);
        await audit.RecordAsync(AuditActions.TotpReset, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        logger.LogWarning(TotpReset, "Authenticator of account {UserId} reset by {AdministratorId}; {Count} session(s) ended", user.Id, CurrentUser.Id, ended);
        return new Result<bool>(true);
    }

    /// <summary>The user, or null when it does not exist, is the break-glass account or reaches beyond the caller's sites.</summary>
    private async Task<User> VisibleAsync(Guid id, CancellationToken ct)
    {
        var user = await GetAsync<User>(id, ct);
        return user is null || user.IsBreakGlass || !await guards.CoversAsync(user, ct) ? null : user;
    }

    internal static UserViewModel View(User user, DateTime now, IEnumerable<string> roles = null, IEnumerable<string> sites = null) => new(
        user.Id.Value,
        user.UserName,
        user.DisplayName,
        user.Email,
        (roles ?? user.Roles.Select(r => r.RoleCode)).Order(StringComparer.Ordinal).ToList(),
        user.IsDisabled,
        user.IsLocked(now),
        user.MustChangePassword,
        user.TotpEnrolled,
        user.LastLoginOn,
        user.CreatedOn,
        user.AllSites,
        (sites ?? user.Sites.Select(s => s.SiteCode)).Order(StringComparer.Ordinal).ToList());

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }
}
