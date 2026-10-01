using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// POST /api/auth/login. Lengths are capped so a huge body cannot make the hash expensive (CWE-400). The attributes sit on
/// the constructor parameters: MVC validates a positional record through them and refuses property-targeted ones.
/// An account with TOTP sends <c>Code</c> (or a <c>RecoveryCode</c>) with the password; without one the answer is
/// 401 mfa_required (ARV-010c).
/// </summary>
public sealed record LoginRequest(
    [Required, MaxLength(256)] string UserName,
    [Required, MaxLength(512)] string Password,
    [MaxLength(16)] string Code = null,
    [MaxLength(32)] string RecoveryCode = null);

/// <summary>POST /api/auth/change-password.</summary>
public sealed record ChangePasswordRequest(
    [Required, MaxLength(512)] string CurrentPassword,
    [Required, MaxLength(512)] string NewPassword);

/// <summary>A TOTP code: confirming an enrolment, or proving the factor before regenerating recovery codes.</summary>
public sealed record TotpCodeRequest([Required, MaxLength(16)] string Code);
