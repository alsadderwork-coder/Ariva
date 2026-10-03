using Ariva.Core.Integration;
using Ariva.Core.Services.Integration;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Integration;

/// <summary>
/// Idempotency keys of Integration API batches (ARV-043, script 0026). The claim is an insert into the request's
/// transaction: when another transaction holds the same key uncommitted, PostgreSQL makes this insert wait until it ends,
/// so two calls with one key never both apply the batch; the second sees the stored answer. An expired key is deleted
/// (by the database's clock, which the table's trigger also uses) and claimed afresh.
/// </summary>
internal sealed class SvcIntegrationIdempotency(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcIntegrationIdempotency
{
    public async Task<IdempotencyClaim> ClaimAsync(IdempotencyRequest request, CancellationToken ct = default)
    {
        Check(request);
        var now = UtcNow;
        // Twice at most: a key found expired is deleted and claimed again; a concurrent claim of it in between wins.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var claimed = await ExecuteCommandAsync<CountRow>("""
                INSERT INTO integration_idempotency (client_id, idempotency_key, operation, site_code, request_sha256, created_utc, expires_utc)
                VALUES (:client, :key, :operation, :site, :sha, :now, :expires)
                ON CONFLICT (client_id, idempotency_key) DO NOTHING
                RETURNING 1 AS "Value"
                """, Parameters(request, ("operation", request.Operation), ("site", request.SiteCode), ("sha", request.RequestSha256), ("now", now),
                    ("expires", now + IntegrationBatches.KeyLifetime)), ct);
            if (claimed.Count > 0)
                return IdempotencyClaim.Claimed;

            var found = await ExecuteCommandAsync<KeyRow>("""
                SELECT operation AS "Operation", site_code AS "SiteCode", request_sha256 AS "RequestSha256", status_code AS "StatusCode",
                       response_body AS "ResponseBody", expires_utc <= :now AS "Expired"
                  FROM integration_idempotency WHERE client_id = :client AND idempotency_key = :key
                """, Parameters(request, ("now", now)), ct);
            if (found.Count == 0)
                continue; // deleted by the sweep in between: claim again
            var row = found[0];
            if (row.Expired)
            {
                await ExecuteCommandAsync<CountRow>("""
                    DELETE FROM integration_idempotency WHERE client_id = :client AND idempotency_key = :key AND expires_utc <= :now RETURNING 1 AS "Value"
                    """, Parameters(request, ("now", now)), ct);
                continue;
            }

            if (!string.Equals(row.Operation, request.Operation, StringComparison.Ordinal) || !string.Equals(row.SiteCode, request.SiteCode, StringComparison.Ordinal) ||
                !string.Equals(row.RequestSha256, request.RequestSha256, StringComparison.Ordinal))
                return IdempotencyClaim.Mismatch;
            // A claim is completed in the transaction that made it, so a visible claim has its answer.
            return row.StatusCode is { } status
                ? new IdempotencyClaim(IdempotencyOutcome.Replay, status, row.ResponseBody)
                : throw new InvalidOperationException("An idempotency key was claimed without an answer.");
        }

        throw new InvalidOperationException("The idempotency key could not be claimed.");
    }

    public async Task CompleteAsync(IdempotencyRequest request, int statusCode, string responseBody, CancellationToken ct = default)
    {
        Check(request);
        ArgumentNullException.ThrowIfNull(responseBody);
        var done = await ExecuteCommandAsync<CountRow>("""
            UPDATE integration_idempotency SET status_code = :status, response_body = :body
             WHERE client_id = :client AND idempotency_key = :key AND status_code IS NULL
            RETURNING 1 AS "Value"
            """, Parameters(request, ("status", statusCode), ("body", responseBody)), ct);
        if (done.Count == 0)
            throw new InvalidOperationException("The idempotency key was not claimed by this call.");
    }

    public async Task<int> SweepAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var deleted = await ExecuteCommandAsync<CountRow>("""
            DELETE FROM integration_idempotency
             WHERE (client_id, idempotency_key) IN (SELECT client_id, idempotency_key FROM integration_idempotency WHERE expires_utc <= now() LIMIT :limit)
               AND expires_utc <= now()
            RETURNING 1 AS "Value"
            """, new Dictionary<string, object> { ["limit"] = limit }, ct);
        return deleted.Count;
    }

    private static void Check(IdempotencyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IntegrationBatches.IsKey(request.Key) || !Core.Domain.Entities.IntegrationClient.IsClientId(request.ClientId) ||
            request.Operation is not (IntegrationBatches.Legs or IntegrationBatches.Events or IntegrationBatches.Allocations or IntegrationBatches.Aidx) ||
            request.RequestSha256 is not { Length: 64 } || string.IsNullOrEmpty(request.SiteCode))
            throw new ArgumentException("Not an idempotency request.", nameof(request));
    }

    // The client and the key, and what else the statement names (NHibernate refuses a parameter the SQL does not use).
    private static Dictionary<string, object> Parameters(IdempotencyRequest request, params (string Name, object Value)[] more)
    {
        var parameters = new Dictionary<string, object>
        {
            ["client"] = request.ClientId,
            ["key"] = request.Key
        };
        foreach (var (name, value) in more)
            parameters[name] = value;
        return parameters;
    }

    private sealed class CountRow
    {
        public int Value { get; set; }
    }

    private sealed class KeyRow
    {
        public string Operation { get; set; }
        public string SiteCode { get; set; }
        public string RequestSha256 { get; set; }
        public int? StatusCode { get; set; }
        public string ResponseBody { get; set; }
        public bool Expired { get; set; }
    }
}
