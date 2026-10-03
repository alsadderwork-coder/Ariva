using System.Net;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;

namespace Ariva.Core.Integration;

/// <summary>An outbound connection, checked (<see cref="OutboundRules.Check"/>).</summary>
public sealed record OutboundConnection(
    Uri BaseUrl,
    IReadOnlyList<string> Networks,
    OutboundAuthKind AuthKind,
    string TokenPath,
    string ClientId,
    string Scope,
    string HeaderName,
    string KeyId,
    bool TotpPerRequest,
    string PinnedCaPem,
    int TimeoutSeconds,
    int RetryCount,
    int BreakerFailures,
    int BreakSeconds,
    string PullPath,
    int PollSeconds);

/// <summary>
/// The rules of outbound connections (ARV-045, CWE-918). Ariva calls only URLs an administrator registered: HTTPS (plain
/// HTTP only to a lab host the deployment lists), no user information, query or fragment in the base URL, token and pull
/// paths relative to it (so the token request and the pull go to the same origin), and every address the host resolves
/// to inside the endpoint's networks and outside the ranges no endpoint may reach (<see cref="AddressRefusal"/>), checked
/// at connection time for the address actually dialled, so DNS rebinding cannot move a call elsewhere.
/// </summary>
public static partial class OutboundRules
{
    public const int MaxTimeoutSeconds = 60;
    public const int MaxRetries = 5;
    public const int MinPollSeconds = 30;
    public const int MaxPollSeconds = 3600;
    public const int MaxPinnedCaChars = 16384;

    /// <summary>Request headers an API key may not be sent as: they are the transport's or another handler's.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Upgrade", "Te", "Trailer", "Keep-Alive", "Proxy-Authorization",
        "Proxy-Connection", "Cookie", "Expect", "X-TOTP-Code", "X-Ariva-Timestamp", "X-Ariva-Signature", "X-Ariva-Key-Id"
    };

    // Ranges no endpoint may reach, whatever its networks say: this host, link-local (cloud metadata), multicast,
    // reserved, and IPv6 forms that carry an IPv4 address the check would otherwise not see.
    private static readonly IPNetwork[] Denied =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("2001::/32")
    ];

    private static readonly IPNetwork[] Loopback = [IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("::1/128")];

    public static bool IsCode(string value) => value is not null && Code().IsMatch(value);

    /// <summary>
    /// Why Ariva may not connect to <paramref name="address"/> for an endpoint with these networks, or null when it may.
    /// Loopback is refused unless <paramref name="allowLoopback"/> (a lab or test setting; never in production).
    /// </summary>
    public static string AddressRefusal(IPAddress address, IReadOnlyList<string> networks, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(networks);
        if (address is null)
            return "No address.";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.Equals(IPAddress.Broadcast))
            return "The broadcast address is never reached.";
        if (Denied.Any(n => n.Contains(address)))
            return "The address is in a range Ariva never calls (this host, link-local or metadata, multicast or reserved).";
        if (Loopback.Any(n => n.Contains(address)) && !allowLoopback)
            return "The address is this host (loopback).";
        return SourceNetworks.Contains(networks, address) ? null : "The address is outside the endpoint's allowed networks.";
    }

    /// <summary>The connection, normalised, or the reasons it cannot be taken.</summary>
    public static (OutboundConnection Connection, IReadOnlyList<string> Errors) Check(OutboundConnectionRequest request, OutboundEndpointPurpose purpose,
        IReadOnlySet<string> labHosts, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(labHosts);
        var errors = new List<string>();
        if (request is null)
            return (null, ["A connection is required."]);

        Uri url = null;
        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var parsed) || parsed.Host.Length is 0 or > 253)
            errors.Add("baseUrl is an absolute URL.");
        else if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            errors.Add("baseUrl has no user information, query or fragment.");
        else if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && labHosts.Contains(parsed.IdnHost)))
            errors.Add("baseUrl is HTTPS (plain HTTP only to a lab host this deployment lists).");
        else
            url = new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");

        var (networks, networkError) = SourceNetworks.Normalize(request.AllowedNetworks);
        if (networkError is not null)
            errors.Add(networkError.Replace("leave the list empty instead", "list the networks the endpoint is in", StringComparison.Ordinal));
        else if (networks.Count == 0)
            errors.Add("allowedNetworks has 1 to 16 networks the endpoint's addresses are in.");
        else if (url is not null && IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var literal) && AddressRefusal(literal, networks, allowLoopback) is { } refusal)
            errors.Add("baseUrl: " + refusal);

        if (!Enum.TryParse<OutboundAuthKind>(request.AuthKind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind) || int.TryParse(request.AuthKind, out _))
        {
            errors.Add("authKind is TotpClientCredentials, OAuth2ClientCredentials, ApiKeyHeader, HmacSignature or MutualTls.");
            kind = default;
        }
        else
        {
            if (kind is OutboundAuthKind.TotpClientCredentials or OutboundAuthKind.OAuth2ClientCredentials)
            {
                if (!IsRelativePath(request.TokenPath, allowQuery: false))
                    errors.Add("tokenPath is a path on the endpoint's own host, such as /api/v1/auth.");
                if (request.ClientId is null || !ClientId().IsMatch(request.ClientId))
                    errors.Add("clientId is 1 to 128 printable characters without spaces.");
            }

            if (kind == OutboundAuthKind.OAuth2ClientCredentials && request.Scope is not null && !Scope().IsMatch(request.Scope))
                errors.Add("scope is up to 200 printable characters, scopes separated by spaces.");
            if (kind == OutboundAuthKind.ApiKeyHeader && (request.HeaderName is null || !HeaderName().IsMatch(request.HeaderName) || ReservedHeaders.Contains(request.HeaderName)))
                errors.Add("headerName is 1 to 64 letters, digits or hyphens, and not a header the transport or another handler sets.");
            if (kind == OutboundAuthKind.HmacSignature && (request.KeyId is null || !KeyId().IsMatch(request.KeyId)))
                errors.Add("keyId is 1 to 64 letters, digits or . _ : - characters.");
        }

        if (request.PinnedCaPem is { } pem && (pem.Length > MaxPinnedCaChars || !pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)))
            errors.Add("pinnedCaPem is one PEM certificate of at most 16 KB.");
        if (request.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            errors.Add("timeoutSeconds is 1 to 60.");
        if (request.RetryCount is < 0 or > MaxRetries)
            errors.Add("retryCount is 0 to 5.");
        if (request.BreakerFailures is < 1 or > 50)
            errors.Add("breakerFailures is 1 to 50.");
        if (request.BreakSeconds is < 5 or > 600)
            errors.Add("breakSeconds is 5 to 600.");

        string pullPath = null;
        if (purpose == OutboundEndpointPurpose.AcrisFlights)
        {
            if (!IsRelativePath(request.PullPath, allowQuery: true))
                errors.Add("pullPath is a path (and query) on the endpoint's own host, such as /acris/flights?airport=DMO.");
            else
                pullPath = request.PullPath;
            if (request.PollSeconds is < MinPollSeconds or > MaxPollSeconds)
                errors.Add("pollSeconds is 30 to 3,600.");
        }
        else if (purpose == OutboundEndpointPurpose.AmanFeed)
        {
            // AMAN's Integration API: TOTP client credentials only (CWE-287), its feed below one path that ends in a slash
            // (the contract names follow it), no query of the endpoint's own.
            if (kind != OutboundAuthKind.TotpClientCredentials)
                errors.Add("An AMAN feed pull authenticates with TotpClientCredentials.");
            else if (!request.TotpPerRequest)
                errors.Add("An AMAN feed pull sends X-TOTP-Code on every call (totpPerRequest).");
            if (!IsRelativePath(request.PullPath, allowQuery: false) || !request.PullPath.EndsWith('/'))
                errors.Add("pullPath is the path of AMAN's feed on the endpoint's own host, ending in a slash, such as /feed/.");
            else
                pullPath = request.PullPath;
            if (request.PollSeconds is < MinPollSeconds or > MaxPollSeconds)
                errors.Add("pollSeconds is 30 to 3,600.");
        }
        else if (request.PullPath is not null)
        {
            errors.Add("pullPath is for the AcrisFlights and AmanFeed purposes only.");
        }

        if (errors.Count > 0)
            return (null, errors);
        var keepsToken = kind is OutboundAuthKind.TotpClientCredentials or OutboundAuthKind.OAuth2ClientCredentials;
        return (new OutboundConnection(url, networks, kind,
            keepsToken ? request.TokenPath : null,
            keepsToken ? request.ClientId : null,
            kind == OutboundAuthKind.OAuth2ClientCredentials ? request.Scope : null,
            kind == OutboundAuthKind.ApiKeyHeader ? request.HeaderName : null,
            kind == OutboundAuthKind.HmacSignature ? request.KeyId : null,
            kind == OutboundAuthKind.TotpClientCredentials && request.TotpPerRequest,
            request.PinnedCaPem, request.TimeoutSeconds, request.RetryCount, request.BreakerFailures, request.BreakSeconds,
            pullPath, purpose is OutboundEndpointPurpose.AcrisFlights or OutboundEndpointPurpose.AmanFeed ? request.PollSeconds : 0), errors);
    }

    /// <summary>The secret's shape for the kind: the fields it uses present and well-formed, the others absent. The certificate itself is checked where it is loaded.</summary>
    public static IReadOnlyList<string> CheckSecret(OutboundSecretRequest secret, OutboundAuthKind kind)
    {
        if (secret is null)
            return ["A secret is required."];
        var errors = new List<string>();
        var uses = kind switch
        {
            OutboundAuthKind.TotpClientCredentials => new[] { nameof(secret.ClientSecret), nameof(secret.TotpSeed) },
            OutboundAuthKind.OAuth2ClientCredentials => [nameof(secret.ClientSecret)],
            OutboundAuthKind.ApiKeyHeader => [nameof(secret.ApiKey)],
            OutboundAuthKind.HmacSignature => [nameof(secret.HmacKey)],
            OutboundAuthKind.MutualTls => [nameof(secret.CertificatePfxBase64), nameof(secret.CertificatePassword)],
            _ => []
        };
        foreach (var (name, value) in new[]
                 {
                     (nameof(secret.ClientSecret), secret.ClientSecret), (nameof(secret.TotpSeed), secret.TotpSeed), (nameof(secret.ApiKey), secret.ApiKey),
                     (nameof(secret.HmacKey), secret.HmacKey), (nameof(secret.CertificatePfxBase64), secret.CertificatePfxBase64)
                 })
        {
            if (!uses.Contains(name) && value is not null)
                errors.Add($"{char.ToLowerInvariant(name[0])}{name[1..]} is not used by {kind}.");
        }

        if (uses.Contains(nameof(secret.ClientSecret)) && (secret.ClientSecret is null || !Printable().IsMatch(secret.ClientSecret)))
            errors.Add("clientSecret is 8 to 256 printable characters.");
        if (uses.Contains(nameof(secret.TotpSeed)) && (secret.TotpSeed is null || !Base32Seed().IsMatch(secret.TotpSeed)))
            errors.Add("totpSeed is the base32 seed the endpoint issued (16 to 128 characters).");
        if (uses.Contains(nameof(secret.ApiKey)) && (secret.ApiKey is null || !Printable().IsMatch(secret.ApiKey)))
            errors.Add("apiKey is 8 to 512 printable characters.");
        if (uses.Contains(nameof(secret.HmacKey)) && !IsHmacKey(secret.HmacKey))
            errors.Add("hmacKey is base64 of at least 32 bytes.");
        if (uses.Contains(nameof(secret.CertificatePfxBase64)) && string.IsNullOrEmpty(secret.CertificatePfxBase64))
            errors.Add("certificatePfxBase64 is the client certificate with its private key, PKCS#12 in base64.");
        return errors;
    }

    private static bool IsHmacKey(string value)
    {
        if (value is null)
            return false;
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) && written >= 32;
    }

    private static bool IsRelativePath(string value, bool allowQuery) =>
        value is not null && (allowQuery ? PathAndQuery() : PathOnly()).IsMatch(value) && !value.Contains("//", StringComparison.Ordinal) &&
        !value.Contains("/../", StringComparison.Ordinal) && !value.EndsWith("/..", StringComparison.Ordinal) && !value.Contains("/./", StringComparison.Ordinal);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,23}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Code();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/-]{0,199}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PathOnly();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/-]{0,199}(\?[A-Za-z0-9._~=&-]{1,99})?\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PathAndQuery();

    [GeneratedRegex(@"^[\x21-\x7E]{1,128}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ClientId();

    [GeneratedRegex(@"^[\x21-\x7E]+( [\x21-\x7E]+)*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Scope();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyId();

    [GeneratedRegex(@"^[\x21-\x7E]{8,512}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Printable();

    [GeneratedRegex(@"^[A-Z2-7]{16,128}=*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Base32Seed();
}
