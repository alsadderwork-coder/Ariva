using Ariva.Core;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Security;

namespace Ariva.Infra.Services.Administration;

/// <summary>
/// Role grants (ARV-011, CWE-269). An administrator cannot change its own roles, cannot grant or revoke a role ranked
/// above its own, cannot touch an account outside its own sites, and cannot take SystemAdministrator from the last
/// active holder. A grant or revoke that would leave an account with every site and the Validation observer role alone is
/// refused (ARV-104a, <see cref="SvcUsers.ObserverSitesProblem"/>). Every grant and revoke is audited in
/// the same transaction and evicts the user's cached permissions after commit; a revoke also ends the user's sessions.
/// The endpoints are critical actions, so the caller has proved a second factor within 15 minutes.
/// </summary>
internal sealed class SvcRoleAssignment(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    CallerRoles callerRoles,
    AdministrationGuards guards,
    AccountSessions sessions,
    AuditTrail audit,
    ILogger<SvcRoleAssignment> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcRoleAssignment
{
    public static readonly EventId RoleGranted = new(9123, "SecurityEvent.RoleGranted");
    public static readonly EventId RoleRevoked = new(9124, "SecurityEvent.RoleRevoked");

    public IReadOnlyList<RoleViewModel> Roles() =>
        RoleCodes.All
            .Select(code => new RoleViewModel(code, RoleHierarchy.Rank(code),
                RolePermissions.ByRole[code].Select(p => p.ToString()).Order(StringComparer.Ordinal).ToList()))
            .ToList();

    public async Task<Result<UserViewModel>> GrantAsync(Guid userId, string roleCode, CancellationToken ct = default)
    {
        var (user, error) = await CheckAsync(userId, roleCode, ct);
        if (error is not null)
            return Result.Error<UserViewModel>(error);
        if (SvcUsers.ObserverSitesProblem(user.AllSites, [.. user.Roles.Select(r => r.RoleCode), roleCode]) is { } sites)
            return Result.Error<UserViewModel>(sites);

        var before = AuditTrail.Summary(user);
        var grant = user.Grant(roleCode, CurrentUser.Id, UtcNow);
        if (grant is null)
            return new Result<UserViewModel>(SvcUsers.View(user, UtcNow));

        await SaveAsync(grant, ct);
        sessions.EvictPermissionsAfterCommit(userId);
        await audit.RecordAsync(AuditActions.RoleGranted, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        logger.LogWarning(RoleGranted, "Role {Role} granted to account {UserId} by {AdministratorId}", roleCode, userId, CurrentUser.Id);
        return new Result<UserViewModel>(SvcUsers.View(user, UtcNow));
    }

    public async Task<Result<UserViewModel>> RevokeAsync(Guid userId, string roleCode, CancellationToken ct = default)
    {
        var (user, error) = await CheckAsync(userId, roleCode, ct);
        if (error is not null)
            return Result.Error<UserViewModel>(error);
        if (!user.Holds(roleCode))
            return new Result<UserViewModel>(SvcUsers.View(user, UtcNow));

        if (roleCode == RoleCodes.SystemAdministrator && await guards.IsLastAdministratorAsync(userId, ct))
            return Result.Error<UserViewModel>(AdministrationErrors.LastAdministrator);
        if (SvcUsers.ObserverSitesProblem(user.AllSites, user.Roles.Select(r => r.RoleCode).Where(r => r != roleCode)) is { } sites)
            return Result.Error<UserViewModel>(sites);

        var before = AuditTrail.Summary(user);
        var grant = user.Revoke(roleCode);
        await DeleteAsync(grant, ct);
        sessions.EvictPermissionsAfterCommit(userId);
        var ended = await sessions.RevokeAllAsync(userId, UtcNow, "role-revoked", Guid.Empty, ct);
        await audit.RecordAsync(AuditActions.RoleRevoked, AuditActions.UserTarget, user.Id, user.UserName, before, AuditTrail.Summary(user), ct);
        logger.LogWarning(RoleRevoked, "Role {Role} revoked from account {UserId} by {AdministratorId}; {Count} session(s) ended", roleCode, userId, CurrentUser.Id, ended);
        return new Result<UserViewModel>(SvcUsers.View(user, UtcNow));
    }

    /// <summary>The target user and the first rule the change breaks, if any.</summary>
    private async Task<(User User, string Error)> CheckAsync(Guid userId, string roleCode, CancellationToken ct)
    {
        if (RoleHierarchy.Rank(roleCode) == 0)
            return (null, AdministrationErrors.UnknownRole);

        if (userId == CurrentUser.Id)
            return (null, AdministrationErrors.OwnAccount);
        var user = await GetAsync<User>(userId, ct);
        if (user is null || user.IsBreakGlass || !await guards.CoversAsync(user, ct))
            return (null, AdministrationErrors.NotFound);
        if (!RoleHierarchy.CanAssign(await callerRoles.GetAsync(ct), roleCode))
            return (null, AdministrationErrors.AboveOwnRole);
        return (user, null);
    }
}
