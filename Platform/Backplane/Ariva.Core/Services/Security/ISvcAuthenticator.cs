using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Security;

/// <summary>
/// Local sign-in and server-side sessions (ADR-0026, ARV-010a and ARV-010b). The boundary OIDC federation plugs into in
/// Phase 1. Every sign-in failure looks the same to the caller: unknown user, wrong password, disabled and locked
/// accounts all return the same error after the same amount of work (CWE-204, CWE-208).
/// </summary>
public interface ISvcAuthenticator : ISvcScoped
{
    /// <summary>The single error message every failed sign-in returns.</summary>
    const string InvalidCredentials = "The username or password is incorrect, or the account cannot sign in.";

    /// <summary>The single error every failed refresh returns (unknown, expired, revoked or reused token).</summary>
    const string SessionExpired = "session_expired";

    /// <summary>The password was right, but the account needs its second factor (a TOTP or recovery code) too.</summary>
    const string MfaRequired = "mfa_required";

    const string InvalidCode = "The code is not valid.";

    const string AlreadyEnrolled = "An authenticator is already enrolled; an administrator can reset it.";

    const string UserNotFound = "The user does not exist.";

    const string CannotChangeOwnAccount = "Administrators cannot disable their own account.";

    /// <summary>Signs in: a new session, refresh family and cookie every time; a refresh cookie sent along is revoked (CWE-384).</summary>
    Task<Result<SignInResult>> LoginAsync(LoginRequest request, SignInContext context, CancellationToken ct = default);

    /// <summary>Rotates the refresh token and returns a new access token for the same session.</summary>
    Task<Result<SignInResult>> RefreshAsync(SignInContext context, CancellationToken ct = default);

    /// <summary>Revokes the caller's session and the session of a refresh cookie sent along. Always succeeds.</summary>
    Task<Result<bool>> LogoutAsync(SignInContext context, CancellationToken ct = default);

    /// <summary>Changes the caller's password and ends the user's other sessions. Returns a fresh token for this session.</summary>
    Task<Result<TokenViewModel>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);

    /// <summary>Administrator unlock (ARV-011 adds the audited user administration around it).</summary>
    Task<Result<bool>> UnlockAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Disables an account and revokes every session it has; access ends within the session cache time.</summary>
    Task<Result<bool>> DisableAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Re-enables an account. The break-glass account is re-enabled only by the installer command.</summary>
    Task<Result<bool>> EnableAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Starts TOTP enrolment for the caller: a new 160-bit secret, returned this once (ARV-010c).</summary>
    Task<Result<TotpEnrolmentViewModel>> EnrolTotpAsync(CancellationToken ct = default);

    /// <summary>Confirms the enrolment with a first code; returns a token without the pending TOTP restriction and the recovery codes.</summary>
    Task<Result<TotpConfirmedViewModel>> ConfirmTotpAsync(TotpCodeRequest request, CancellationToken ct = default);

    /// <summary>New recovery codes after a valid TOTP code; the old ones stop working.</summary>
    Task<Result<RecoveryCodesViewModel>> RegenerateRecoveryCodesAsync(TotpCodeRequest request, CancellationToken ct = default);
}
