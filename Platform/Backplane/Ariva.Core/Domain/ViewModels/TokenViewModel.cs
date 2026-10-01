namespace Ariva.Core.Domain.ViewModels;

/// <summary>
/// The access token returned by sign-in and password change (ADR-0026). The refresh token is never in a body; it
/// arrives as an HttpOnly cookie with ARV-010b.
/// </summary>
public sealed record TokenViewModel(string AccessToken, string TokenType, int ExpiresIn, string Scope);
