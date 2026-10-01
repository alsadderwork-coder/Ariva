namespace Ariva.Core.Domain.ViewModels;

/// <summary>
/// The access token returned by sign-in, refresh and password change (ADR-0026). The refresh token is never in a body:
/// it travels only in the HttpOnly __Secure-ariva_rt cookie (ARV-010b).
/// </summary>
public sealed record TokenViewModel(string AccessToken, string TokenType, int ExpiresIn, string Scope);

/// <summary>
/// What sign-in and refresh hand to the controller: the body, and the refresh token with its lifetime for the cookie.
/// Never serialised as a whole.
/// </summary>
public sealed record SignInResult(TokenViewModel Token, string RefreshToken, TimeSpan RefreshLifetime);
