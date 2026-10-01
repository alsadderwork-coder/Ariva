namespace Ariva.Core;

/// <summary>
/// Platform wide constants, following AMAN's Global.cs. Permission codes are declared as constants so that
/// controllers, policies and the <c>Permission</c> attribute reference names instead of string literals.
/// </summary>
public static class Global
{
    /// <summary>
    /// Permission codes in the form "Area.Entity.Action". Add one nested static class per area as stories
    /// introduce endpoints, for example:
    /// <code>
    /// public static class Zones
    /// {
    ///     public const string View = "Ops.Zones.View";
    ///     public const string Edit = "Admin.Zones.Edit";
    /// }
    /// </code>
    /// </summary>
    public static class Permissions
    {
    }
}
