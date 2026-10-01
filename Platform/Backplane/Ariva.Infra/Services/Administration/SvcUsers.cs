using Ariva.Core;
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
    AccountSessions sessions,
    AuditTrail audit,
    ILogger<SvcUsers> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcUsers
{
    public static readonly EventId UserCreated = new(9120, "SecurityEvent.UserCreated");
    public static readonly EventId PasswordReset = new(9121, "SecurityEvent.PasswordReset");
    public static readonly EventId TotpReset = new(9122, "SecurityEvent.TotpReset");

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

        var userName = UserNames.Normalize(request.UserName);
        if (await Query<User>().AnyAsync(u => u.UserName == userName, ct))
            return Result.Error<UserCreatedViewModel>(AdministrationErrors.UserNameTaken);

        var now = UtcNow;
        var password = TemporaryPasswords.New();
        var user = new User(userName, request.DisplayName?.Trim(), request.Email);
        user.SetPassword(PasswordHasher.Hash(password), temporary: true);
        await SaveAsync(user, ct);
        foreach (var role in roles)
            await SaveAsync(user.Grant(role, CurrentUser.Id, now), ct);

        await audit.RecordAsync(AuditActions.UserCreated, AuditActions.UserTarget, user.Id, user.UserName, null, AuditTrail.Summary(user), ct);
        logger.LogInformation(UserCreated, "Account {UserId} created by {AdministratorId} with roles {Roles}", user.Id, CurrentUser.Id, string.Join(",", roles));
        return new Result<UserCreatedViewModel>(new UserCreatedViewModel(View(user, now), password));
    }

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

        var query = QueryAsNoTracking<User>().Where(u => !u.IsBreakGlass);
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
        var now = UtcNow;
        var data = users.Select(u => View(u, now, grants[u.Id])).ToList();
        return new Result<PageViewModel<UserViewModel>>(new PageViewModel<UserViewModel>(data, total, pageIndex, pageSize));
    }

    public async Task<Result<UserViewModel>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await VisibleAsync(id, ct);
        if (user is null)
            return Result.Error<UserViewModel>(AdministrationErrors.NotFound);

        var before = AuditTrail.Summary(user);
        user.UpdateProfile(request?.DisplayName, request?.Email);
        await UpdateAsync(user, ct);
        await audit.RecordAsync(AuditActions.UserUpdated, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        return new Result<UserViewModel>(View(user, UtcNow));
    }

    public async Task<Result<TemporaryPasswordViewModel>> ResetPasswordAsync(Guid id, CancellationToken ct = default)
    {
        var user = await VisibleAsync(id, ct);
        if (user is null)
            return Result.Error<TemporaryPasswordViewModel>(AdministrationErrors.NotFound);
        if (user.Id == CurrentUser.Id)
            return Result.Error<TemporaryPasswordViewModel>(AdministrationErrors.OwnAccount);

        var before = AuditTrail.Summary(user);
        var password = TemporaryPasswords.New();
        user.SetPassword(PasswordHasher.Hash(password), temporary: true);
        user.Unlock();
        await UpdateAsync(user, ct);
        var ended = await sessions.RevokeAllAsync(user.Id.Value, UtcNow, "password-reset", Guid.Empty, ct);
        await audit.RecordAsync(AuditActions.PasswordReset, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        logger.LogWarning(PasswordReset, "Password of account {UserId} reset by {AdministratorId}; {Count} session(s) ended", user.Id, CurrentUser.Id, ended);
        return new Result<TemporaryPasswordViewModel>(new TemporaryPasswordViewModel(password));
    }

    public async Task<Result<bool>> ResetTotpAsync(Guid id, CancellationToken ct = default)
    {
        var user = await VisibleAsync(id, ct);
        if (user is null)
            return Result.Error<bool>(AdministrationErrors.NotFound);
        if (user.Id == CurrentUser.Id)
            return Result.Error<bool>(AdministrationErrors.OwnAccount);

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

    /// <summary>The user, or null when it does not exist or is the break-glass account.</summary>
    private async Task<User> VisibleAsync(Guid id, CancellationToken ct)
    {
        var user = await GetAsync<User>(id, ct);
        return user is null || user.IsBreakGlass ? null : user;
    }

    internal static UserViewModel View(User user, DateTime now, IEnumerable<string> roles = null) => new(
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
        user.CreatedOn);

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }
}
