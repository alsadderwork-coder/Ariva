using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Security;

/// <summary>
/// Local username and password sign-in (ADR-0026, ARV-010a). The boundary OIDC federation plugs into in Phase 1.
/// Every failure looks the same to the caller: unknown user, wrong password, disabled and locked accounts all return
/// the same error after the same amount of work (CWE-204, CWE-208).
/// </summary>
public interface ISvcAuthenticator : ISvcScoped
{
    /// <summary>The single error message every failed sign-in returns.</summary>
    const string InvalidCredentials = "The username or password is incorrect, or the account cannot sign in.";

    Task<Result<TokenViewModel>> LoginAsync(LoginRequest request, CancellationToken ct = default);

    /// <summary>Changes the caller's password; clears a temporary password. Returns a fresh token.</summary>
    Task<Result<TokenViewModel>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);

    /// <summary>Administrator unlock (ARV-011 adds the audited user administration around it).</summary>
    Task<Result<bool>> UnlockAsync(Guid userId, CancellationToken ct = default);
}
