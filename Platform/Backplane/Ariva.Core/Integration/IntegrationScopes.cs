using System.Net;

namespace Ariva.Core.Integration;

/// <summary>
/// The scopes an integration client may hold (ARV-042, docs/architecture/integration.md): each allows one family of
/// Integration API endpoints. Fixed here; a client is granted a subset by an administrator.
/// </summary>
public static class IntegrationScopes
{
    public const string FlightsWrite = "flights:write";
    public const string AllocationsWrite = "allocations:write";
    public const string ImmigrationWrite = "immigration:write";
    public const string SensingWrite = "sensing:write";
    public const string QueuesRead = "queues:read";
    public const string DisplaysRead = "displays:read";

    public static IReadOnlyList<string> All { get; } = [FlightsWrite, AllocationsWrite, ImmigrationWrite, SensingWrite, QueuesRead, DisplaysRead];

    public static bool IsKnown(string scope) => scope is not null && System.Linq.Enumerable.Contains(All, scope, StringComparer.Ordinal);
}

/// <summary>
/// Source networks in CIDR form (ARV-022, ARV-042): at most 16 blocks, each with its prefix, no host bits set below the
/// prefix (10.0.0.1/24 is a likely typo) and no /0 (leave the list empty to allow every address).
/// </summary>
public static class SourceNetworks
{
    public const int MaxBlocks = 16;

    /// <summary>The blocks, normalised and distinct, or the reason they cannot be taken.</summary>
    public static (IReadOnlyList<string> Blocks, string Error) Normalize(IEnumerable<string> blocks)
    {
        var given = (blocks ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (given.Count > MaxBlocks)
            return ([], $"At most {MaxBlocks} source networks.");
        var normalised = new List<string>();
        foreach (var block in given)
        {
            var shown = block.Length > 50 ? block[..50] : block;
            if (!IPNetwork.TryParse(block, out var network) || !block.Contains('/', StringComparison.Ordinal))
                return ([], $"'{shown}' is not a network in CIDR form, for example 10.20.0.0/24.");
            if (!IPAddress.TryParse(block[..block.IndexOf('/', StringComparison.Ordinal)], out var address) || !address.Equals(network.BaseAddress))
                return ([], $"'{shown}' has host bits set; the network is {network}.");
            if (network.PrefixLength == 0)
                return ([], "A /0 network allows every address; leave the list empty instead.");
            if (network.BaseAddress.IsIPv4MappedToIPv6)
                return ([], $"'{shown}' is an IPv4-mapped IPv6 network; give it in IPv4 form.");
            var text = network.ToString();
            if (!System.Linq.Enumerable.Contains(normalised, text, StringComparer.Ordinal))
                normalised.Add(text);
        }

        return (normalised, null);
    }

    /// <summary>True when <paramref name="address"/> is inside one of the blocks (an IPv4-mapped IPv6 address counts as IPv4).</summary>
    public static bool Contains(IReadOnlyList<string> blocks, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        foreach (var block in blocks)
        {
            if (IPNetwork.TryParse(block, out var network) && network.Contains(address))
                return true;
        }

        return false;
    }
}
