using NHibernate.Linq;

namespace Ariva.Infra.Services.Administration;

/// <summary>
/// The caller's role codes, read from the stored grants (tokens never carry roles). Used for the rank check on grants
/// (ARV-011); a disabled or unknown caller holds nothing.
/// </summary>
internal sealed class CallerRoles(IUnitOfWork unitOfWork, ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken ct = default)
    {
        if (currentUser.Id is not { } id)
            return [];
        return await unitOfWork.StorageProvider.Query<UserRole>()
            .Where(r => r.User.Id == id && !r.User.IsDisabled)
            .Select(r => r.RoleCode)
            .ToListAsync(ct);
    }
}
