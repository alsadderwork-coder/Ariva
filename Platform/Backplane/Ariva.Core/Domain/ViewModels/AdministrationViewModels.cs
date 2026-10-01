namespace Ariva.Core.Domain.ViewModels;

/// <summary>An account as administrators see it. Never carries a password hash, TOTP secret or recovery code.</summary>
public sealed record UserViewModel(
    Guid Id,
    string UserName,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    bool IsDisabled,
    bool IsLocked,
    bool MustChangePassword,
    bool TotpEnrolled,
    DateTime? LastLoginOn,
    DateTime? CreatedOn,
    bool AllSites,
    IReadOnlyList<string> Sites);

/// <summary>A created account and its temporary password, shown once.</summary>
public sealed record UserCreatedViewModel(UserViewModel User, string TemporaryPassword);

/// <summary>A new temporary password after a reset, shown once.</summary>
public sealed record TemporaryPasswordViewModel(string TemporaryPassword);

/// <summary>A role with its rank and the permissions it grants.</summary>
public sealed record RoleViewModel(string Code, int Rank, IReadOnlyList<string> Permissions);

/// <summary>One audit entry.</summary>
public sealed record AuditEntryViewModel(
    Guid Id,
    DateTime OccurredOn,
    Guid? ActorId,
    string ActorName,
    string Action,
    string TargetType,
    Guid? TargetId,
    string TargetName,
    string BeforeSummary,
    string AfterSummary,
    string IpAddress,
    string TraceId);

/// <summary>A page of results.</summary>
public sealed record PageViewModel<T>(IReadOnlyList<T> Data, int TotalCount, int PageIndex, int PageSize);

/// <summary>A site (ARV-012).</summary>
public sealed record SiteViewModel(string Code, string Name, DateTime? CreatedOn);
