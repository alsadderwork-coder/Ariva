using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>A new account (ARV-011). The server generates a temporary password, shown once; the user changes it at first sign-in.</summary>
public sealed record CreateUserRequest(
    [Required, MaxLength(64)] string UserName,
    [MaxLength(200)] string DisplayName = null,
    [MaxLength(320), EmailAddress] string Email = null,
    [MaxLength(4)] IReadOnlyList<string> Roles = null);

/// <summary>Profile fields an administrator can change; roles, password and TOTP have their own audited actions.</summary>
public sealed record UpdateUserRequest(
    [MaxLength(200)] string DisplayName = null,
    [MaxLength(320), EmailAddress] string Email = null);
