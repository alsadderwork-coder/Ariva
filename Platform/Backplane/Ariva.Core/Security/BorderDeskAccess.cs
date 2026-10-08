using static Ariva.Core.Global.Defaults;

namespace Ariva.Core.Security;

/// <summary>
/// Who reaches desk-level validation data (ARV-104b; data boundary, CWE-863): the one rule every desk path of the validation
/// services applies, from the caller's stored roles. A desk code beside a minute is border data, so:
/// <list type="bullet">
/// <item><see cref="Sees"/>: a manager's reads (the desks in a campaign's scope, every observer's desk states) and putting desks
/// in a campaign answer only a caller who holds <c>BorderDesks.View</c> (border shift supervisors and administrators).</item>
/// <item><see cref="Observes"/>: the observer's side (the desks listed in the capture view, a desk batch, a correction, the
/// caller's own states) answers a caller who holds <c>BorderDesks.View</c>, or one who holds no role but
/// <see cref="RoleCodes.ValidationObserver"/> (the border's own observer). An account that also holds an airport-side role
/// (<see cref="IsAirportSide"/>: terminal duty manager, handler station manager, or any role that is neither border nor
/// administrator nor observer) and lacks <c>BorderDesks.View</c> is refused (403) and sees no desk, whatever observer role it
/// holds besides: first security review of ARV-104b, 2026-10-08, option (b).</item>
/// </list>
/// No role at all reaches nothing (default deny).
/// </summary>
/// <param name="Sees">The caller holds <c>BorderDesks.View</c>.</param>
/// <param name="Observes">The caller may log, correct and read back its own desk states.</param>
public readonly record struct BorderDeskAccess(bool Sees, bool Observes)
{
    /// <summary>The caller's access from its role codes (null and repeated codes are ignored; unknown codes count as airport-side).</summary>
    public static BorderDeskAccess Of(IEnumerable<string> roles)
    {
        var held = (roles ?? []).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToList();
        if (held.Count == 0)
            return new BorderDeskAccess(false, false);
        var sees = RolePermissions.For(held).Contains(Permissions.ViewBorderDesks);
        return new BorderDeskAccess(sees, sees || !held.Any(IsAirportSide));
    }

    /// <summary>
    /// A role on the airport side of the data boundary: anything but the border shift supervisor, the system administrator and
    /// the validation observer (so a role added later is airport-side until it is placed deliberately).
    /// </summary>
    public static bool IsAirportSide(string role) =>
        role is not (RoleCodes.BorderShiftSupervisor or RoleCodes.SystemAdministrator or RoleCodes.ValidationObserver);
}
