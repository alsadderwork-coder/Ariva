using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;

namespace Ariva.Core.Services.Border;

/// <summary>
/// Where every immigration record enters Ariva (ARV-048): AMAN's Kafka topics and the immigration endpoints of the
/// Integration API hand the four AMAN feed contracts V1 here. Each record is checked (<see cref="ImmigrationRules"/>) and
/// stored on its own; a bad record never stops the others. A record is kept once per site by its source event id, so a
/// redelivery or the same record over both transports changes nothing. Desk and gate codes are the border system's own
/// and are resolved through the site's AMAN desk code mappings; an unmapped code is kept apart with a warning, never
/// guessed. <see cref="ImmigrationScope"/> says which sites the call may write (the REST path's site, or for Kafka the
/// site each record names, which must exist); an unchosen scope is refused. System calls: the caller has already
/// authenticated the feed.
/// </summary>
public interface ISvcImmigrationIntake : ISvcScoped
{
    Task<IReadOnlyList<ImmigrationItemResult>> ApplyDeskSessionsAsync(ImmigrationScope scope, string feed, IReadOnlyList<DeskSessionChanged> items, CancellationToken ct = default);

    Task<IReadOnlyList<ImmigrationItemResult>> ApplyDeskIntervalsAsync(ImmigrationScope scope, string feed, IReadOnlyList<DeskIntervalStats> items, CancellationToken ct = default);

    Task<IReadOnlyList<ImmigrationItemResult>> ApplyEgateIntervalsAsync(ImmigrationScope scope, string feed, IReadOnlyList<EGateIntervalStats> items, CancellationToken ct = default);

    /// <summary>Lane demand per inbound flight: the latest computation wins, an older one changes nothing.</summary>
    Task<IReadOnlyList<ImmigrationItemResult>> ApplyLaneDemandAsync(ImmigrationScope scope, string feed, IReadOnlyList<InboundFlightLaneDemand> items, CancellationToken ct = default);
}
