using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Api.Integration.Batches;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Integration;
using Ariva.Core.Services.Border;
using Ariva.Core.Services.Integration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Integration.Controllers;

/// <summary>
/// The immigration endpoints of the Integration API (ARV-048, docs/architecture/integration.md, wiki 08): the four AMAN
/// feed contracts V1 for one site, sent by AMAN or any immigration system, in batches like the flight endpoints
/// (<c>{ "messageTimeUtc"?, "items": [...] }</c>, 1 to 500 items, at most 1 MB, <c>Idempotency-Key</c>, read strictly: no
/// unknown member, enums by exact name, times with an offset). The site comes from the route and is one of the client's; a record naming another site
/// is refused. Each record is checked and stored on its own (<see cref="ISvcImmigrationIntake"/>): 200 with every
/// record's result (key, applied, errors, warnings), also when some or all were refused; a record already received is
/// unchanged. The same answers as the flight batches for a body that is not a batch (400), too large (413), not JSON
/// (415), a reused key (422) and the rate limits (429).
/// </summary>
[ApiController]
[Route("api/v1/integration/sites/{siteCode}/immigration")]
[EnableRateLimiting(RateLimitingExtensions.IntegrationBatchPolicy)]
public sealed class ImmigrationFeedController(ISvcImmigrationIntake intake, ISvcIntegrationIdempotency idempotency, TimeProvider timeProvider) : ControllerBase
{
    [HttpPost("desk-sessions")]
    [IntegrationScope(IntegrationScopes.ImmigrationWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxBodyBytes)]
    [ProducesResponseType<ImmigrationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> DeskSessions(string siteCode, CancellationToken ct) =>
        BatchAsync<DeskSessionChanged>(siteCode, IntegrationBatches.DeskSessions, (feed, items) => intake.ApplyDeskSessionsAsync(ImmigrationScope.ForSite(siteCode), feed, items, ct), ct);

    [HttpPost("desk-interval-stats")]
    [IntegrationScope(IntegrationScopes.ImmigrationWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxBodyBytes)]
    [ProducesResponseType<ImmigrationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> DeskIntervals(string siteCode, CancellationToken ct) =>
        BatchAsync<DeskIntervalStats>(siteCode, IntegrationBatches.DeskIntervals, (feed, items) => intake.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite(siteCode), feed, items, ct), ct);

    [HttpPost("egate-interval-stats")]
    [IntegrationScope(IntegrationScopes.ImmigrationWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxBodyBytes)]
    [ProducesResponseType<ImmigrationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> EgateIntervals(string siteCode, CancellationToken ct) =>
        BatchAsync<EGateIntervalStats>(siteCode, IntegrationBatches.EgateIntervals, (feed, items) => intake.ApplyEgateIntervalsAsync(ImmigrationScope.ForSite(siteCode), feed, items, ct), ct);

    [HttpPost("inbound-lane-demand")]
    [IntegrationScope(IntegrationScopes.ImmigrationWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxBodyBytes)]
    [ProducesResponseType<ImmigrationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> LaneDemand(string siteCode, CancellationToken ct) =>
        BatchAsync<InboundFlightLaneDemand>(siteCode, IntegrationBatches.LaneDemand, (feed, items) => intake.ApplyLaneDemandAsync(ImmigrationScope.ForSite(siteCode), feed, items, ct), ct);

    private Task<IActionResult> BatchAsync<T>(string siteCode, string operation, Func<string, IReadOnlyList<T>, Task<IReadOnlyList<ImmigrationItemResult>>> apply,
        CancellationToken ct) =>
        BatchCall.HandleAsync<T>(this, siteCode, operation, idempotency, timeProvider, BatchBody.StrictWithEnumNames,
            async (feed, batch) => ImmigrationBatchViewModel.Of(await apply(feed, batch.Items)), ct);
}
