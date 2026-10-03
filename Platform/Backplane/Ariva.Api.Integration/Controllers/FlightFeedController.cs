using System.Security.Cryptography;
using System.Text.Json;
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
public sealed class FlightFeedController(ISvcFlightIntake intake, ISvcIntegrationIdempotency idempotency, IntegrationClientRateLimiter clientLimiter, TimeProvider timeProvider)
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

    private async Task<IActionResult> BatchAsync<T>(string siteCode, string operation,
        Func<string, IReadOnlyList<T>, DateTime?, Task<IReadOnlyList<FlightItemResult>>> apply, CancellationToken ct)
    {
        var caller = IntegrationAuthentication.CallerOf(HttpContext);
        if (caller is null)
            return Forbid();
        var (allowed, retryAfter) = clientLimiter.TryAcquire(caller.ClientId);
        if (!allowed)
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling((retryAfter ?? TimeSpan.FromSeconds(60)).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests",
                detail: "This client sent more batches than it may in a minute; retry later with the same Idempotency-Key.");
        }

        var keys = Request.Headers[IntegrationBatches.IdempotencyKeyHeader];
        if (keys.Count != 1 || !IntegrationBatches.IsKey(keys[0]))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid",
                detail: $"One {IntegrationBatches.IdempotencyKeyHeader} header of 8 to 64 letters, digits or . _ : - characters is required (a UUID fits).");
        // Checked here rather than with [Consumes]: a mismatch there leaves no endpoint, and the caller would get a 404.
        if (!BatchBody.IsJson(Request.ContentType))
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported media type", detail: "A batch is application/json.");

        byte[] body;
        try
        {
            body = await BatchBody.ReadAsync(Request, ct);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            body = null;
        }

        if (body is null)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: "A batch is at most 1 MB.");

        var (batch, error) = BatchBody.Parse<T>(body);
        if (error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: error);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (batch.MessageTimeUtc is { } sent && !FlightRules.IsPlausibleSource(sent, now))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid",
                detail: "messageTimeUtc is UTC (ending in Z), at most 5 minutes ahead of Ariva's clock and 30 days behind it.");

        var request = new IdempotencyRequest(caller.ClientId, keys[0], operation, siteCode, Convert.ToHexStringLower(SHA256.HashData(body)));
        var claim = await idempotency.ClaimAsync(request, ct);
        switch (claim.Outcome)
        {
            case IdempotencyOutcome.Mismatch:
                return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Key reused",
                    detail: $"This {IntegrationBatches.IdempotencyKeyHeader} was used for another request in the last 24 hours; use a new key for a new batch.");
            case IdempotencyOutcome.Replay:
                Response.Headers[IntegrationBatches.ReplayedHeader] = "true";
                return new ContentResult { StatusCode = claim.StatusCode, Content = claim.ResponseBody, ContentType = "application/json; charset=utf-8" };
        }

        var results = await apply(IntegrationBatches.FeedOf(caller.ClientId), batch.Items, batch.MessageTimeUtc);
        var answer = JsonSerializer.Serialize(IntegrationBatchViewModel.Of(results), JsonSerializerOptions.Web);
        await idempotency.CompleteAsync(request, StatusCodes.Status200OK, answer, ct);
        return new ContentResult { StatusCode = StatusCodes.Status200OK, Content = answer, ContentType = "application/json; charset=utf-8" };
    }
}
