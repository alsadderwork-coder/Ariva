using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Device authentication (ARV-022): a scheme of its own, apart from users (ES256 access tokens) and integration clients.
/// A device presents its credential as <c>Authorization: Bearer ardk_...</c>, as the <c>X-Ariva-Device-Key</c> header, or
/// as HTTP Basic with its code as the user name (for vendor push settings that only offer Basic). Endpoints opt in with
/// <see cref="DeviceAuthenticatedAttribute"/>; they accept nothing else, so a user token there answers 401, and a device
/// credential anywhere else answers 401 too.
/// </summary>
public static class DeviceAuthentication
{
    public const string Scheme = "Ariva.Device";
    public const string Policy = "Ariva.Device";
    public const string OwnZonePolicy = "Ariva.Device.OwnZone";
    public const string KeyHeader = "X-Ariva-Device-Key";

    /// <summary>HttpContext.Items key of the authenticated device's record (placement and dialect for the ingest).</summary>
    public const string RecordItem = "ariva:device-record";

    /// <summary>The authenticated device's record, or null outside a device request.</summary>
    public static DeviceCredentialRecord RecordOf(HttpContext context) =>
        context?.Items.TryGetValue(RecordItem, out var record) == true ? record as DeviceCredentialRecord : null;

    /// <summary>The route value a device-zone endpoint names its zone with.</summary>
    public const string ZoneRouteValue = "zone";

    public const string DeviceIdClaim = "ariva:device_id";
    public const string DeviceCodeClaim = "ariva:device";
    public const string SiteClaim = "ariva:site";
    public const string ZoneClaim = "ariva:zone";
    public const string StateClaim = "ariva:device_state";

    /// <summary>Registers the device scheme and its two policies (any device; a device on its own zone).</summary>
    public static AuthenticationBuilder AddArivaDeviceAuthentication(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(Scheme, displayName: null, configureOptions: null);
        builder.Services.AddSingleton<IAuthorizationHandler, DeviceZoneHandler>();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(DeviceIdClaim))
            .AddPolicy(OwnZonePolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(DeviceIdClaim)
                .AddRequirements(new DeviceZoneRequirement()));
        return builder;
    }

    /// <summary>The presented credential and, for Basic, the user name; nulls when the request carries none.</summary>
    public static (string Key, string BasicUser) Presented(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.TryGetValue(KeyHeader, out var header) && header.Count == 1)
            return (header[0], null);

        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } value)
            return (null, null);
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = value["Bearer ".Length..].Trim();
            return token.StartsWith(DeviceCredentials.Marker, StringComparison.Ordinal) ? (token, null) : (null, null);
        }

        if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) && value.Length <= 200)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value["Basic ".Length..].Trim()));
                var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                    return (decoded[(colon + 1)..], decoded[..colon]);
            }
            catch (FormatException)
            {
                // Not base64: no credential.
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The rate limit partition of a device request (before authentication, without a database): a hash of the whole
    /// presented credential when it is well formed, the client address otherwise. Not the prefix: the prefix is shown to
    /// administrators and in audit entries, and someone who knows it could otherwise exhaust a real sensor's window with
    /// made-up keys; only the holder of the whole credential lands in that device's window.
    /// </summary>
    public static string RateLimitPartition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (key, _) = Presented(context.Request);
        if (DeviceCredentials.IsWellFormed(key))
            return "device:" + DeviceCredentials.Hash(key)[..32];
        var address = context.Connection.RemoteIpAddress;
        return "address:" + (address is null ? "unknown" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString());
    }

    /// <summary>The authenticated device's site and queue zone, or nulls for anyone else.</summary>
    public static (string Code, string Site, string Zone, string State) DeviceOf(ClaimsPrincipal user) =>
        user?.Identity is { IsAuthenticated: true, AuthenticationType: Scheme }
            ? (user.FindFirstValue(DeviceCodeClaim), user.FindFirstValue(SiteClaim), user.FindFirstValue(ZoneClaim), user.FindFirstValue(StateClaim))
            : (null, null, null, null);
}

/// <summary>
/// Marks an endpoint for devices only (ARV-022). With <paramref name="ownZoneOnly"/>, the route value "zone" must be the
/// device's own queue zone, or the answer is 403.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeviceAuthenticatedAttribute : AuthorizeAttribute
{
    public DeviceAuthenticatedAttribute(bool ownZoneOnly = false)
    {
        Policy = ownZoneOnly ? DeviceAuthentication.OwnZonePolicy : DeviceAuthentication.Policy;
        AuthenticationSchemes = DeviceAuthentication.Scheme;
    }
}

/// <summary>The route's zone is the device's own queue zone.</summary>
public sealed class DeviceZoneRequirement : IAuthorizationRequirement;

public sealed class DeviceZoneHandler : AuthorizationHandler<DeviceZoneRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DeviceZoneRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.Resource as HttpContext;
        var zone = http?.GetRouteValue(DeviceAuthentication.ZoneRouteValue) as string;
        var own = context.User.FindFirstValue(DeviceAuthentication.ZoneClaim);
        if (zone is not null && own is not null && string.Equals(zone, own, StringComparison.Ordinal))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Checks a presented device credential (ARV-022): well formed, the device behind its prefix found (cached briefly,
/// evicted on every registry change), the SHA-256 compared in constant time, the Basic user name equal to the device
/// code, the client address inside the device's allowed networks, and the pinned client certificate presented when
/// one is pinned. A failure says nothing about why to the caller (401); the log names the device prefix and the reason,
/// never the credential.
/// </summary>
public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (key, basicUser) = DeviceAuthentication.Presented(Request);
        if (key is null)
            return AuthenticateResult.NoResult();

        var check = await DeviceCredentialCheck.VerifyAsync(Context.RequestServices.GetRequiredService<ISvcDeviceGateway>(), key, basicUser,
            Context.Connection.RemoteIpAddress, async ct => Context.Connection.ClientCertificate ?? await Context.Connection.GetClientCertificateAsync(ct),
            Context.RequestAborted);
        if (check.Device is null)
            return Refuse(check.Prefix, check.Refusal);

        var device = check.Device;
        Context.Items[DeviceAuthentication.RecordItem] = device;
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceAuthentication.DeviceIdClaim, device.DeviceId.ToString()),
            new Claim(DeviceAuthentication.DeviceCodeClaim, device.Code),
            new Claim(DeviceAuthentication.SiteClaim, device.SiteCode),
            new Claim(DeviceAuthentication.ZoneClaim, device.QueueZoneName),
            new Claim(DeviceAuthentication.StateClaim, device.State)
        ], DeviceAuthentication.Scheme, DeviceAuthentication.DeviceCodeClaim, null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), DeviceAuthentication.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"ariva-devices\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private AuthenticateResult Refuse(string prefix, string reason)
    {
        Logger.LogWarning("Device authentication refused for {CredentialPrefix} from {Address}: {Reason}", prefix ?? "(none)", Context.Connection.RemoteIpAddress, reason);
        return AuthenticateResult.Fail("Device authentication failed.");
    }
}

/// <summary>
/// The checks on a presented device credential (ARV-022), shared by the HTTPS scheme and the MQTT transport (ARV-024):
/// well formed, the device behind its prefix found (cached briefly, evicted on every registry change), the SHA-256
/// compared in constant time (a miss costs the same hash), the user name, when one is given, equal to the device code,
/// the client address inside the device's allowed networks, and the pinned client certificate presented when one is
/// pinned. The refusal reason is for the log only, never for the caller.
/// </summary>
public static class DeviceCredentialCheck
{
    private static readonly string NoDeviceHash = new('0', 64);

    public sealed record Outcome(DeviceCredentialRecord Device, string Prefix, string Refusal);

    public static async Task<Outcome> VerifyAsync(ISvcDeviceGateway gateway, string key, string userName, IPAddress remote,
        Func<CancellationToken, Task<System.Security.Cryptography.X509Certificates.X509Certificate2>> certificate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(certificate);
        if (!DeviceCredentials.IsWellFormed(key))
            return new Outcome(null, null, "malformed credential");

        var prefix = key[..DeviceCredentials.PrefixLength];
        var device = await gateway.FindByPrefixAsync(prefix, ct);
        var matches = DeviceCredentials.Matches(key, device?.CredentialHash ?? NoDeviceHash);
        if (device is null || !matches)
            return new Outcome(null, prefix, "unknown or wrong credential");
        if (userName is not null && !string.Equals(userName, device.Code, StringComparison.Ordinal))
            return new Outcome(null, prefix, "user name is not the device code");
        if (device.AllowedSources.Count > 0 && !FromAllowedNetwork(remote, device.AllowedSources))
            return new Outcome(null, prefix, "client address outside the device's allowed networks");
        if (device.ClientCertificateSha256 is { } pin && !Pinned(await certificate(ct), pin))
            return new Outcome(null, prefix, "pinned client certificate not presented");
        return new Outcome(device, prefix, null);
    }

    public static bool FromAllowedNetwork(IPAddress address, IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        foreach (var block in allowed)
        {
            if (IPNetwork.TryParse(block, out var network) && network.Contains(address))
                return true;
        }

        return false;
    }

    private static bool Pinned(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, string pin)
    {
        if (certificate is null)
            return false;
        var presented = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(pin));
    }
}
