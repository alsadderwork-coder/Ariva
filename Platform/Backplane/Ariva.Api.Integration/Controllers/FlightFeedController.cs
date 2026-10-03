using Ariva.Api.Common.Security;
using Ariva.Api.Integration.Batches;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Integration;
using Ariva.Api.Common.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Integration.Controllers;

/// <summary>
/// The Integration API for any AODB (ARV-043, docs/architecture/integration.md, wiki 08): flight legs, flight events and
/// check-in counter allocations for one site, in batches of 1 to 500 items and at most 1 MB, each with an
/// <c>Idempotency-Key</c>. The site comes from the route and is one of the client's (checked by [IntegrationScope] before
/// the action runs); the feed is the client's own (<see cref="IntegrationBatches.FeedOf"/>); nothing in the body chooses
/// either. Each item is checked and applied on its own (<see cref="ISvcFlightIntake"/>): 200 with every item's result,
/// also when some or all were refused. 400 for a body that is not a batch, 413 beyond 1 MB, 415 for anything but JSON,
/// 422 for a key already used for another request. A retry with the same key and body gets the first answer again with
/// <c>Idempotent-Replayed: true</c>, and nothing is applied twice. 429 with <c>Retry-After</c> beyond the batches handled
/// at once per host (before any body is read) or beyond the client's own allowance per minute; retry with the same key.
/// </summary>
[ApiController]
[Route("api/v1/integration/sites/{siteCode}")]
[EnableRateLimiting(RateLimitingExtensions.IntegrationBatchPolicy)]
public sealed class FlightFeedController(ISvcFlightIntake intake, ISvcIntegrationIdempotency idempotency, TimeProvider timeProvider)
    : ControllerBase
{
    [HttpPost("flights/batch")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxBodyBytes)]
    [ProducesResponseType<IntegrationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Legs(string siteCode, CancellationToken ct) =>
        BatchAsync<FlightLegData>(siteCode, IntegrationBatches.Legs, (feed, items, source) => intake.ApplyLegsAsync(siteCode, feed, items, source, ct), ct);

    [HttpPost("flights/events")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxBodyBytes)]
    [ProducesResponseType<IntegrationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Events(string siteCode, CancellationToken ct) =>
        BatchAsync<FlightEventData>(siteCode, IntegrationBatches.Events, (feed, items, source) => intake.ApplyEventsAsync(siteCode, feed, items, source, ct), ct);

    [HttpPost("allocations/batch")]
    [IntegrationScope(IntegrationScopes.AllocationsWrite)]
    [RequestSizeLimit(IntegrationBatches.MaxBodyBytes)]
    [ProducesResponseType<IntegrationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Allocations(string siteCode, CancellationToken ct) =>
        BatchAsync<CounterAllocationData>(siteCode, IntegrationBatches.Allocations,
            (feed, items, source) => intake.ApplyAllocationsAsync(siteCode, feed, items, source, ct), ct);

    private Task<IActionResult> BatchAsync<T>(string siteCode, string operation,
        Func<string, IReadOnlyList<T>, DateTime?, Task<IReadOnlyList<FlightItemResult>>> apply, CancellationToken ct) =>
        BatchCall.HandleAsync<T>(this, siteCode, operation, idempotency, timeProvider, BatchBody.Strict,
            async (feed, batch) => IntegrationBatchViewModel.Of(await apply(feed, batch.Items, batch.MessageTimeUtc)), ct);
}
