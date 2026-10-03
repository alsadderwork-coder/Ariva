using System.Text.RegularExpressions;
using Ariva.Core.Flights;

namespace Ariva.Core.Integration;

/// <summary>
/// The Integration API's batch endpoints (ARV-043, docs/architecture/integration.md): a JSON body of at most 1 MB with an
/// optional message time and 1 to 500 items, sent with an <c>Idempotency-Key</c>. The site comes from the route (bound to
/// the client by [IntegrationScope]) and the feed from the client, never from the body.
/// </summary>
public static partial class IntegrationBatches
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const int MaxItems = FlightRules.MaxBatch;
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>How long a key is kept: a retry within it gets the first answer.</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

    /// <summary>Operations a key is claimed for; a key reused for another operation or site is refused.</summary>
    public const string Legs = "flights.legs";
    public const string Events = "flights.events";
    public const string Allocations = "allocations";
    public const string Aidx = "aodb.aidx";

    /// <summary>An AIDX 22.1 message is at most 5 MB (ARV-044).</summary>
    public const int MaxAidxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// An idempotency key: 8 to 64 letters, digits or <c>. _ : -</c>, starting with a letter or digit (a UUID fits).
    /// Short keys are refused so that two batches cannot collide by accident.
    /// </summary>
    public static bool IsKey(string value) => value is not null && Key().IsMatch(value);

    /// <summary>
    /// The feed name of a client's batches: <c>api-</c> and the client id's 26 characters, so each client's data and
    /// freshness stay its own (the stale-feed alarm names the client that went silent).
    /// </summary>
    public static string FeedOf(string clientId) =>
        Domain.Entities.IntegrationClient.IsClientId(clientId) ? "api-" + clientId[3..] : throw new ArgumentException("Not a client id.", nameof(clientId));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Key();
}

/// <summary>A batch body: when the AODB produced it (optional; UTC) and its items.</summary>
public sealed record IntegrationBatch<T>(DateTime? MessageTimeUtc, IReadOnlyList<T> Items);

/// <summary>
/// The answer to a batch: how many items it carried, applied (changed something), left unchanged (nothing newer than
/// what was known) and refused, with each item's result in order.
/// </summary>
public sealed record IntegrationBatchViewModel(int Received, int Applied, int Unchanged, int Refused, IReadOnlyList<FlightItemResult> Items)
{
    public static IntegrationBatchViewModel Of(IReadOnlyList<FlightItemResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var refused = items.Count(i => i.HasErrors);
        var applied = items.Count(i => i.Applied);
        return new IntegrationBatchViewModel(items.Count, applied, items.Count - applied - refused, refused, items);
    }
}
