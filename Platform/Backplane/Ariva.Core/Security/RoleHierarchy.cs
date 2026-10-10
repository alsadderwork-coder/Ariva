namespace Ariva.Core.Security;

/// <summary>
/// Role ranks for grants (ARV-011, CWE-269): an administrator can grant or revoke a role only when one of its own roles
/// ranks at least as high, and never on its own account. The operational roles share one rank; SystemAdministrator
/// outranks them. The validation observer (ARV-104a) ranks with the operational roles. Custom roles are a Phase 1
/// candidate; the five fixed roles keep the escalation surface small.
/// </summary>
public static class RoleHierarchy
{
    private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [RoleCodes.BorderShiftSupervisor] = 1,
        [RoleCodes.TerminalDutyManager] = 1,
        [RoleCodes.HandlerStationManager] = 1,
        [RoleCodes.ValidationObserver] = 1,
        [RoleCodes.SystemAdministrator] = 2
    };

    /// <summary>The rank of a role code; 0 for an unknown code.</summary>
    public static int Rank(string roleCode) => roleCode is not null && Ranks.TryGetValue(roleCode, out var rank) ? rank : 0;

    /// <summary>The highest rank among the roles; 0 for none.</summary>
    public static int Highest(IEnumerable<string> roleCodes) => (roleCodes ?? []).Select(Rank).DefaultIfEmpty(0).Max();

    /// <summary>True when a holder of <paramref name="granterRoles"/> may grant or revoke <paramref name="roleCode"/>.</summary>
    public static bool CanAssign(IEnumerable<string> granterRoles, string roleCode)
    {
        var rank = Rank(roleCode);
        return rank > 0 && Highest(granterRoles) >= rank;
    }
}
