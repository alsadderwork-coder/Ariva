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

    Task<Result<bool>> EnableAsync(Guid userId, CancellationToken ct = default);
}
