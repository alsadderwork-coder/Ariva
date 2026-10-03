namespace Ariva.Core.Domain.ViewModels;

/// <summary>
/// The access token returned by sign-in, refresh and password change (ADR-0026). The refresh token is never in a body:
/// it travels only in the HttpOnly __Secure-ariva_rt cookie (ARV-010b).
/// </summary>
public sealed record TokenViewModel(string AccessToken, string TokenType, int ExpiresIn, string Scope)
{
    /// <summary>Set when the sign-in used a recovery code: how many are left (regenerate them soon).</summary>
    public int? RecoveryCodesRemaining { get; init; }
}

/// <summary>
/// The signed-in user as the web app needs it (ARV-051): names, roles and the permissions they grant ("Entity.Action";
/// none while the account is pending), the sites (or all), and what the first sign-in still needs (a new password, an
/// authenticator). The server stays the authority: the web only uses this to choose what to show.
/// </summary>
public sealed record CurrentUserViewModel(
    string UserName,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool AllSites,
    IReadOnlyList<string> Sites,
    bool MustChangePassword,
    bool TotpEnrolled,
    bool Pending);

/// <summary>A started TOTP enrolment: the secret as base32 text and the otpauth URI the web app shows as a QR code. Shown once.</summary>
public sealed record TotpEnrolmentViewModel(string Secret, string OtpAuthUri);

/// <summary>A confirmed enrolment: the new token (second factor done) and the recovery codes, shown once.</summary>
public sealed record TotpConfirmedViewModel(TokenViewModel Token, IReadOnlyList<string> RecoveryCodes);

/// <summary>Newly generated recovery codes, shown once; the previous ones no longer work.</summary>
public sealed record RecoveryCodesViewModel(IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// What sign-in and refresh hand to the controller: the body, and the refresh token with its lifetime for the cookie.
/// Never serialised as a whole.
/// </summary>
public sealed record SignInResult(TokenViewModel Token, string RefreshToken, TimeSpan RefreshLifetime);
