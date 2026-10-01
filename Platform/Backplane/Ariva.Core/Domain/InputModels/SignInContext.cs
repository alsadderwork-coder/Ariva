namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// Request details sign-in and refresh need besides the body: the client address and user agent stored on the session,
/// and the refresh token cookie the browser sent, if any (login revokes it; refresh rotates it).
/// </summary>
public sealed record SignInContext(string IpAddress, string UserAgent, string PresentedRefreshToken);
