namespace Ariva.Api.Common.Settings;

/// <summary>
/// Origins allowed to call the APIs from a browser, bound from <c>Security:Cors</c>. Origins are listed exactly
/// (scheme, host and port, no path, no trailing slash); a wildcard is rejected at startup because the policy
/// allows credentials for the refresh cookie.
/// </summary>
public sealed class CorsSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Security:Cors";

    /// <summary>Allowed origins, for example <c>https://dev-ariva.dalilhub.tech</c>. Empty means no cross origin access.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Whether browsers may send credentials (the HttpOnly refresh cookie). Default true.</summary>
    public bool AllowCredentials { get; set; } = true;

    /// <summary>How long browsers may cache a preflight answer, in seconds. Default 600.</summary>
    public int PreflightMaxAgeSeconds { get; set; } = 600;

    /// <summary>True when every origin is an exact http or https origin and none is a wildcard.</summary>
    public bool IsValid => PreflightMaxAgeSeconds >= 0 && (AllowedOrigins ?? []).All(IsExactOrigin);

    private static bool IsExactOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return string.Equals(origin, $"{uri.Scheme}://{uri.Authority}", StringComparison.OrdinalIgnoreCase);
    }
}
