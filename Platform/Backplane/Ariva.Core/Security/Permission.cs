namespace Ariva.Core.Security;

/// <summary>
/// What a permission allows on its entity. Every entity gets the five CRUD-style actions (ARV-009), except the few
/// process entities with actions of their own (PermissionModelTests lists them).
/// </summary>
public enum PermissionAction
{
    View,
    Create,
    Edit,
    Search,
    Delete,

    /// <summary>Makes a draft version the active one (zone profiles); critical, needs step-up MFA (ADR-0026).</summary>
    Publish,

    /// <summary>Records ground truth (ARV-104a): manual counts for a running validation campaign, and their corrections.</summary>
    Capture,

    /// <summary>Runs a process (ARV-104a): creates, starts and closes validation campaigns; closing is critical (step-up MFA).</summary>
    Manage
}

/// <summary>
/// A permission, identified by its stable code "Entity.Action" (for example "ZoneProfile.Publish"). Codes are stored
/// in role grants and audit rows, so a shipped code never changes. Controllers reference permissions by property name
/// through <c>[Permission(nameof(Global.Defaults.Permissions.ViewZoneProfile))]</c>, never by role.
/// </summary>
public sealed record Permission(string Entity, PermissionAction Action)
{
    public string Code => $"{Entity}.{Action}";

    /// <summary>The <c>Global.Defaults.Permissions</c> property name: action then entity, as in AMAN (ViewZoneProfile).</summary>
    public string Name => $"{Action}{Entity}";

    public override string ToString() => Code;
}
