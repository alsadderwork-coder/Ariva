namespace Ariva.Core.Domain.Constants;

/// <summary>Stable action codes in audit_entry (ARV-011); never rename one that has shipped.</summary>
public static class AuditActions
{
    public const string UserCreated = "User.Created";
    public const string UserUpdated = "User.Updated";
    public const string RoleGranted = "User.RoleGranted";
    public const string RoleRevoked = "User.RoleRevoked";
    public const string PasswordReset = "User.PasswordReset";
    public const string TotpReset = "User.TotpReset";
    public const string UserUnlocked = "User.Unlocked";
    public const string UserDisabled = "User.Disabled";
    public const string UserEnabled = "User.Enabled";

    public const string UserTarget = "User";

    public static readonly IReadOnlyList<string> All =
        [UserCreated, UserUpdated, RoleGranted, RoleRevoked, PasswordReset, TotpReset, UserUnlocked, UserDisabled, UserEnabled];
}
