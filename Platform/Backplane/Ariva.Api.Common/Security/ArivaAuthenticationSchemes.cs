namespace Ariva.Api.Common.Security;

/// <summary>
/// Authentication scheme names used by the Ariva hosts.
/// </summary>
public static class ArivaAuthenticationSchemes
{
    /// <summary>
    /// Placeholder scheme registered by <c>AddAppSecurity</c>. It never authenticates anyone, so every endpoint
    /// that is not explicitly anonymous answers 401 (CWE-862, CWE-306). The authentication story replaces it with
    /// the JWT bearer schemes (separate keys and audiences for users, integration clients and devices) and makes
    /// the user scheme the default.
    /// </summary>
    public const string Deny = "Ariva.Deny";
}
