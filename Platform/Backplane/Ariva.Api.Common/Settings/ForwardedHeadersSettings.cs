using System.Net;

namespace Ariva.Api.Common.Settings;

/// <summary>
/// Reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers are trusted, bound from
/// <c>Security:ForwardedHeaders</c>. Nothing is trusted by default, not even loopback; in Kubernetes list the
/// ingress controller's pod network so rate limits and logs see the real client address.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Security:ForwardedHeaders";

    /// <summary>Single proxy addresses, for example <c>10.1.2.3</c>.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR notation, for example <c>10.244.0.0/16</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Most forwarded entries processed. Default 1: only the entry added by the trusted proxy.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>True when every address and network parses and the limit is positive.</summary>
    public bool IsValid =>
        ForwardLimit > 0
        && (KnownProxies ?? []).All(proxy => IPAddress.TryParse(proxy, out _))
        && (KnownNetworks ?? []).All(network => System.Net.IPNetwork.TryParse(network, out _));
}
