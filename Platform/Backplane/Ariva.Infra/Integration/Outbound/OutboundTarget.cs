using System.Net;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;

namespace Ariva.Infra.Integration.Outbound;

/// <summary>What a host needs to call an outbound endpoint (ARV-045), read from its record; the secret is still protected.</summary>
public sealed record OutboundTarget(
    Guid Id,
    string Code,
    int Version,
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
    string SecretProtected)
{
    public static OutboundTarget Of(OutboundEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new OutboundTarget(endpoint.Id!.Value, endpoint.Code, endpoint.ClientVersion, new Uri(endpoint.BaseUrl), endpoint.Networks, endpoint.AuthKind, endpoint.TokenPath,
            endpoint.ClientId, endpoint.Scope, endpoint.HeaderName, endpoint.KeyId, endpoint.TotpPerRequest, endpoint.PinnedCaPem, endpoint.TimeoutSeconds, endpoint.RetryCount,
            endpoint.BreakerFailures, endpoint.BreakSeconds, endpoint.SecretProtected);
    }

    /// <summary>Never printed with the protected secret.</summary>
    public override string ToString() => $"OutboundTarget {Code} v{Version}";
}

/// <summary>A call Ariva refused to make (CWE-918): an address outside the endpoint's networks or in a range no endpoint may reach, or a redirect.</summary>
public sealed class OutboundRefusedException(string message) : HttpRequestException(message);

/// <summary>The endpoint's circuit is open after repeated failures: no call is made until it closes.</summary>
public sealed class OutboundUnavailableException(string message) : HttpRequestException(message);

/// <summary>Resolves a host name to its addresses; replaceable in tests (DNS rebinding).</summary>
public interface IOutboundResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

internal sealed class DnsOutboundResolver : IOutboundResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}
