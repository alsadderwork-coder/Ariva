namespace Ariva.Infra.Security;

/// <summary>The role codes a user holds, read from storage (a disabled user holds none), for the live hub's alert groups (ARV-039).</summary>
public class UserRoles(IUnitOfWork unitOfWork)
{
    public virtual async Task<IReadOnlyList<string>> ForUserAsync(Guid userId, CancellationToken ct = default) =>
        await global::NHibernate.Linq.LinqExtensionMethods.ToListAsync(
            unitOfWork.StorageProvider.Query<UserRole>().Where(r => r.User.Id == userId && !r.User.IsDisabled).Select(r => r.RoleCode), ct);
}
