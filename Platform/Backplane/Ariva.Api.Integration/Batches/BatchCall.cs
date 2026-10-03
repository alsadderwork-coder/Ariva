using System.Security.Cryptography;
using System.Text.Json;
using Ariva.Api.Common.Security;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
using Ariva.Core.Services.Integration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Integration.Batches;

/// <summary>
/// One Integration API batch call (ARV-043, ARV-048), the same for every JSON batch endpoint: the caller from the
/// integration scheme, one <c>Idempotency-Key</c>, JSON only, at most 1 MB, the body read strictly, a plausible
/// <c>messageTimeUtc</c>, the key claimed for the client, operation, site and body (a replay answers the stored answer
/// with <c>Idempotent-Replayed: true</c>, another request under the key is 422), then the items applied as the client's
/// feed and the answer stored with the key.
/// </summary>
public static class BatchCall
{
    public static async Task<IActionResult> HandleAsync<T>(ControllerBase controller, string siteCode, string operation, ISvcIntegrationIdempotency idempotency,
        TimeProvider timeProvider, JsonSerializerOptions options, Func<string, IntegrationBatch<T>, Task<object>> apply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(apply);
        var http = controller.HttpContext;
        var caller = IntegrationAuthentication.CallerOf(http);
        if (caller is null)
            return controller.Forbid();

        var keys = http.Request.Headers[IntegrationBatches.IdempotencyKeyHeader];
        if (keys.Count != 1 || !IntegrationBatches.IsKey(keys[0]))
            return controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid",
                detail: $"One {IntegrationBatches.IdempotencyKeyHeader} header of 8 to 64 letters, digits or . _ : - characters is required (a UUID fits).");
        // Checked here rather than with [Consumes]: a mismatch there leaves no endpoint, and the caller would get a 404.
        if (!BatchBody.IsJson(http.Request.ContentType))
            return controller.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported media type", detail: "A batch is application/json.");

        byte[] body;
        try
        {
            body = await BatchBody.ReadAsync(http.Request, ct);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            body = null;
        }

        if (body is null)
            return controller.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: "A batch is at most 1 MB.");

        var (batch, error) = BatchBody.Parse<T>(body, options);
        if (error is not null)
            return controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: error);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (batch.MessageTimeUtc is { } sent && !FlightRules.IsPlausibleSource(sent, now))
            return controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid",
                detail: "messageTimeUtc is UTC (ending in Z), at most 5 minutes ahead of Ariva's clock and 30 days behind it.");

        var request = new IdempotencyRequest(caller.ClientId, keys[0], operation, siteCode, Convert.ToHexStringLower(SHA256.HashData(body)));
        var claim = await idempotency.ClaimAsync(request, ct);
        switch (claim.Outcome)
        {
            case IdempotencyOutcome.Mismatch:
                return controller.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Key reused",
                    detail: $"This {IntegrationBatches.IdempotencyKeyHeader} was used for another request in the last 24 hours; use a new key for a new batch.");
            case IdempotencyOutcome.Replay:
                http.Response.Headers[IntegrationBatches.ReplayedHeader] = "true";
                return new ContentResult { StatusCode = claim.StatusCode, Content = claim.ResponseBody, ContentType = "application/json; charset=utf-8" };
        }

        var result = await apply(IntegrationBatches.FeedOf(caller.ClientId), batch);
        var answer = JsonSerializer.Serialize(result, result.GetType(), JsonSerializerOptions.Web);
        await idempotency.CompleteAsync(request, StatusCodes.Status200OK, answer, ct);
        return new ContentResult { StatusCode = StatusCodes.Status200OK, Content = answer, ContentType = "application/json; charset=utf-8" };
    }
}
