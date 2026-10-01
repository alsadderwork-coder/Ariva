using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// POST /api/auth/login. Lengths are capped so a huge body cannot make the hash expensive (CWE-400). The attributes sit on
/// the constructor parameters: MVC validates a positional record through them and refuses property-targeted ones.
/// </summary>
public sealed record LoginRequest(
    [Required, MaxLength(256)] string UserName,
    [Required, MaxLength(512)] string Password);

/// <summary>POST /api/auth/change-password.</summary>
public sealed record ChangePasswordRequest(
    [Required, MaxLength(512)] string CurrentPassword,
    [Required, MaxLength(512)] string NewPassword);
