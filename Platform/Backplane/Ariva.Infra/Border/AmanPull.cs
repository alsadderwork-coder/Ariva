using System.Text.Json;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Services.Border;
using Ariva.Infra.Integration.Outbound;
using Ariva.Infra.Services.Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Border;

/// <summary>The four AMAN feed contracts as AMAN's Integration API names them in its feed paths (ARV-050, wiki 08 section 10).</summary>
public static class AmanPullContracts
{
    public const string DeskSessions = "desk-sessions";
    public const string DeskIntervals = "desk-interval-stats";
    public const string EgateIntervals = "egate-interval-stats";
    public const string LaneDemand = "inbound-lane-demand";

    public static IReadOnlyList<string> All { get; } = [DeskSessions, DeskIntervals, EgateIntervals, LaneDemand];
}

/// <summary>One page of AMAN's feed as read (ARV-050): the records that were readable, how many were not, and AMAN's next position.</summary>
public sealed record AmanFeedPage<T>(IReadOnlyList<T> Items, int Unreadable, long Next);

/// <summary>
/// Reads one page of AMAN's feed from its Integration API (ARV-050): AMAN's envelope
/// <c>{ "data": { "items": [...], "next": n }, "hasErrors": false, "errorMessages": [] }</c>, each item read as strictly as the
/// Kafka values (<see cref="AmanFeedJson{T}"/>: exact members, exact enum names, explicit offsets); an unreadable item is
/// counted, never echoed. The page is refused (with Ariva's own reason, never AMAN's text) when the envelope is not that,
/// when AMAN reports errors, when it holds more than <see cref="MaxItems"/> items, or when its position does not move
/// forward from <c>after</c> (or moves without items).
/// </summary>
public static class AmanFeedPages
{
    /// <summary>Items one page may hold (Ariva asks for 500).</summary>
    public const int MaxItems = 1_000;

    public const int PageSize = 500;

    public static (AmanFeedPage<T> Page, string Error) Read<T>(byte[] body, long after) where T : class
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "AMAN's answer is not its feed envelope.");
            if (root.TryGetProperty("hasErrors", out var hasErrors) && hasErrors.ValueKind == JsonValueKind.True)
                return (null, "AMAN answered with errors.");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                !data.TryGetProperty("next", out var nextElement) || !nextElement.TryGetInt64(out var next))
                return (null, "AMAN's answer is not its feed envelope.");
            var count = items.GetArrayLength();
            if (count > MaxItems)
                return (null, $"AMAN's page holds more than {MaxItems} records.");
            if (next < after || (count > 0 && next == after) || (count == 0 && next != after))
                return (null, "AMAN's feed position did not move forward with its records.");
            var read = new List<T>(count);
            var unreadable = 0;
            foreach (var item in items.EnumerateArray())
            {
                T value = null;
                try
                {
                    value = item.ValueKind == JsonValueKind.Object ? item.Deserialize<T>(AmanFeedJson<T>.Options) : null;
                }
                catch (JsonException)
                {
                }
                catch (NotSupportedException)
                {
                }

                if (value is null)
                    unreadable++;
                else
                    read.Add(value);
            }

            return (new AmanFeedPage<T>(read, unreadable, next), null);
        }
        catch (JsonException)
        {
            return (null, "AMAN's answer is not JSON.");
        }
    }
}

/// <summary>A due AMAN pull claimed by this replica: the endpoint, its feed path, the site it feeds.</summary>
public sealed record AmanPullClaim(OutboundTarget Target, string PullPath, string SiteCode);

/// <summary>
/// The database side of the AMAN pull (ARV-050): claims due AmanFeed endpoints (one replica each), reads and saves the
/// position in each contract, applies a page through <see cref="ISvcImmigrationIntake"/> for the endpoint's one site
/// (<see cref="ImmigrationScope.ForPull"/>, the feed <c>aman-</c> and the endpoint's code) in the caller's unit of work
/// together with the new position, and records how a pull went. The HTTP side is <see cref="AmanPoller"/>.
/// </summary>
internal sealed class SvcAmanPull(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISvcImmigrationIntake intake)
    : SvcBase(unitOfWork, currentUser, timeProvider)
{
    /// <summary>
    /// Claims the due endpoints. An endpoint failing in a row waits longer (its interval times one more than its failures,
    /// at most ten times), so a wrong secret does not spend AMAN's lockout at the full rate.
    /// </summary>
    public async Task<IReadOnlyList<AmanPullClaim>> ClaimDueAsync(int max, CancellationToken ct)
    {
        var claimed = await ExecuteCommandAsync<IdRow>("""
            UPDATE outbound_endpoint SET last_poll_utc = :now
             WHERE id IN (SELECT id FROM outbound_endpoint
                           WHERE purpose = 'AmanFeed' AND status = 'Active' AND pull_path IS NOT NULL
                             AND (last_poll_utc IS NULL
                                  OR last_poll_utc <= :now - poll_seconds * LEAST(1 + consecutive_failures, 10) * interval '1 second')
                           ORDER BY last_poll_utc NULLS FIRST LIMIT :max FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Id"
            """, new Dictionary<string, object> { ["now"] = UtcNow, ["max"] = max }, ct);
        var claims = new List<AmanPullClaim>();
        foreach (var row in claimed)
        {
            var endpoint = await GetAsync<OutboundEndpoint>(row.Id, ct);
            if (endpoint is { Purpose: Core.Domain.Enums.OutboundEndpointPurpose.AmanFeed, AuthKind: Core.Domain.Enums.OutboundAuthKind.TotpClientCredentials } &&
                endpoint.Sites.Count == 1)
                claims.Add(new AmanPullClaim(OutboundTarget.Of(endpoint), endpoint.PullPath, endpoint.Sites[0]));
        }

        return claims;
    }

    /// <summary>The position in each contract (0 for a contract never pulled).</summary>
    public async Task<IReadOnlyDictionary<string, long>> CursorsAsync(Guid endpointId, CancellationToken ct)
    {
        var rows = await ExecuteSqlAsync<CursorRow>("""
            SELECT contract AS "Contract", after_sequence AS "After" FROM aman_pull_cursor WHERE endpoint_id = :id
            """, new Dictionary<string, object> { ["id"] = endpointId }, ct);
        return AmanPullContracts.All.ToDictionary(c => c, c => rows.FirstOrDefault(r => r.Contract == c)?.After ?? 0, StringComparer.Ordinal);
    }

    /// <summary>Applies one page and moves the position, in the caller's unit of work: (applied, unchanged, refused).</summary>
    public async Task<(int Applied, int Unchanged, int Refused)> ApplyAsync(AmanPullClaim claim, string contract, object page, long next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var scope = ImmigrationScope.ForPull(claim.SiteCode);
        var feed = "aman-" + claim.Target.Code;
        IReadOnlyList<ImmigrationItemResult> results = page switch
        {
            AmanFeedPage<DeskSessionChanged> p => await intake.ApplyDeskSessionsAsync(scope, feed, p.Items, ct),
            AmanFeedPage<DeskIntervalStats> p => await intake.ApplyDeskIntervalsAsync(scope, feed, p.Items, ct),
            AmanFeedPage<EGateIntervalStats> p => await intake.ApplyEgateIntervalsAsync(scope, feed, p.Items, ct),
            AmanFeedPage<InboundFlightLaneDemand> p => await intake.ApplyLaneDemandAsync(scope, feed, p.Items, ct),
            _ => throw new ArgumentException("An AMAN feed page.", nameof(page))
        };
        await ExecuteCommandAsync<IdRow>("""
            INSERT INTO aman_pull_cursor (endpoint_id, contract, after_sequence, updated_on) VALUES (:id, :contract, :next, :now)
            ON CONFLICT (endpoint_id, contract) DO UPDATE SET after_sequence = EXCLUDED.after_sequence, updated_on = EXCLUDED.updated_on
             WHERE aman_pull_cursor.after_sequence < EXCLUDED.after_sequence
            RETURNING endpoint_id AS "Id"
            """, new Dictionary<string, object> { ["id"] = claim.Target.Id, ["contract"] = contract, ["next"] = next, ["now"] = UtcNow }, ct);
        var applied = results.Count(r => r.Applied);
        var refused = results.Count(r => r.HasErrors);
        return (applied, results.Count - applied - refused, refused);
    }

    public async Task RecordAsync(Guid id, bool success, string status, CancellationToken ct)
    {
        var text = status is { Length: > 200 } ? status[..200] : status ?? string.Empty;
        await ExecuteCommandAsync<IdRow>("""
            UPDATE outbound_endpoint
               SET last_status = :status,
                   consecutive_failures = CASE WHEN :success THEN 0 ELSE consecutive_failures + 1 END
             WHERE id = :id
            RETURNING id AS "Id"
            """, new Dictionary<string, object>
        {
            ["status"] = new string(text.Where(c => !char.IsControl(c)).ToArray()), ["success"] = success, ["id"] = id
        }, ct);
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    private sealed class CursorRow
    {
        public string Contract { get; set; }
        public long After { get; set; }
    }
}

/// <summary>
/// Pulls AMAN's feed from its Integration API (ARV-050, wiki 08 section 10) for every active AmanFeed endpoint when it is
/// due, where AMAN's Kafka is not shared: claims it (one replica per endpoint), and for each contract asks
/// <c>GET {pullPath}{contract}?after={position}&amp;limit=500</c> through the endpoint's guarded client (TOTP client
/// credentials: the token cached and renewed by <see cref="TokenHandler"/>, <c>X-TOTP-Code</c> on every call when the
/// endpoint needs it; the SSRF control and the resilience of ARV-045), up to <see cref="MaxPagesPerPoll"/> pages, applying
/// each page with its new position in one unit of work, then records the outcome on the endpoint. Records AMAN also sent
/// over another transport are unchanged (the intake keeps one per site and source event id).
/// </summary>
internal sealed class AmanPoller(IServiceScopeFactory scopes, OutboundClients clients, TimeProvider time, Microsoft.Extensions.Configuration.IConfiguration configuration,
    ILogger<AmanPoller> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    public const int MaxPagesPerPoll = 20;
    private const int ClaimsPerTick = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Integration:Outbound:PollAman false turns the pull off in this host (tests that start the host without a database).
        if (!Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(configuration, "Integration:Outbound:PollAman", true))
            return;
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one failed round (the database restarting) must not stop the poller; the next tick retries
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "AMAN pull round failed; retrying at the next tick");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One round: claim due endpoints and pull each. Public for tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<AmanPullClaim> claims;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claims = await scope.ServiceProvider.GetRequiredService<SvcAmanPull>().ClaimDueAsync(ClaimsPerTick, ct);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(ct);
        }

        await Task.WhenAll(claims.Select(claim => PullAsync(claim, ct)));
        return claims.Count;
    }

    private async Task PullAsync(AmanPullClaim claim, CancellationToken ct)
    {
        var success = false;
        string status;
        var (records, applied, unchanged, refused, unreadable) = (0, 0, 0, 0, 0);
        try
        {
            IReadOnlyDictionary<string, long> cursors;
            await using (var scope = scopes.CreateAsyncScope())
                cursors = await scope.ServiceProvider.GetRequiredService<SvcAmanPull>().CursorsAsync(claim.Target.Id, ct);

            string error = null;
            foreach (var contract in AmanPullContracts.All)
            {
                var after = cursors[contract];
                for (var page = 0; page < MaxPagesPerPoll && error is null; page++)
                {
                    var (read, next, problem) = await PageAsync(claim, contract, after, ct);
                    if (problem is not null)
                    {
                        error = problem;
                        break;
                    }

                    if (read.Count + read.Unreadable == 0)
                        break;
                    var outcome = await ApplyAsync(claim, contract, read.Page, next, ct);
                    (records, applied, unchanged, refused, unreadable) =
                        (records + read.Count + read.Unreadable, applied + outcome.Applied, unchanged + outcome.Unchanged, refused + outcome.Refused, unreadable + read.Unreadable);
                    after = next;
                    if (read.Count + read.Unreadable < AmanFeedPages.PageSize)
                        break;
                }

                if (error is not null)
                    break;
            }

            var summary = $"Pulled {records} AMAN records: {applied} applied, {unchanged} unchanged, {refused} refused, {unreadable} unreadable.";
            (success, status) = error is null ? (true, summary) : (false, $"{error} {summary}");
            if (unreadable > 0 || refused > 0)
                logger.LogWarning("AMAN pull from {Endpoint}: {Refused} records refused, {Unreadable} unreadable", claim.Target.Code, refused, unreadable);
        }
        catch (OutboundRefusedException e)
        {
            status = e.Message;
            logger.LogWarning("AMAN pull from {Endpoint} refused: {Reason}", claim.Target.Code, e.Message);
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            status = "The endpoint's secret cannot be read with this key ring; set the secret again.";
            logger.LogError(e, "AMAN pull from {Endpoint}: its secret cannot be unprotected", claim.Target.Code);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            status = e is OutboundUnavailableException ? e.Message
                : e is HttpRequestException { StatusCode: { } code } ? $"AMAN answered {(int)code}."
                : "AMAN could not be reached or did not answer in time.";
            logger.LogWarning(e, "AMAN pull from {Endpoint} failed", claim.Target.Code);
        }
#pragma warning disable CA1031 // anything else (the database, an unexpected answer) is recorded on the endpoint too, then the next poll tries again
        catch (Exception e) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            status = "The pull failed in Ariva; see the Integration log.";
            logger.LogError(e, "AMAN pull from {Endpoint} failed in Ariva", claim.Target.Code);
        }

        await using var record = scopes.CreateAsyncScope();
        await record.ServiceProvider.GetRequiredService<SvcAmanPull>().RecordAsync(claim.Target.Id, success, status, ct);
        await record.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(ct);
    }

    private sealed record PageRead(object Page, int Count, int Unreadable);

    private async Task<(PageRead Read, long Next, string Error)> PageAsync(AmanPullClaim claim, string contract, long after, CancellationToken ct)
    {
        var path = $"{claim.PullPath.TrimStart('/')}{contract}?after={after.ToString(System.Globalization.CultureInfo.InvariantCulture)}&limit={AmanFeedPages.PageSize}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await clients.For(claim.Target).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return (null, after, $"AMAN answered {(int)response.StatusCode} for {contract}.");
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        return contract switch
        {
            AmanPullContracts.DeskSessions => Wrap(AmanFeedPages.Read<DeskSessionChanged>(body, after)),
            AmanPullContracts.DeskIntervals => Wrap(AmanFeedPages.Read<DeskIntervalStats>(body, after)),
            AmanPullContracts.EgateIntervals => Wrap(AmanFeedPages.Read<EGateIntervalStats>(body, after)),
            _ => Wrap(AmanFeedPages.Read<InboundFlightLaneDemand>(body, after))
        };

        (PageRead, long, string) Wrap<T>((AmanFeedPage<T> Page, string Error) result) => result.Error is not null
            ? (null, after, $"{result.Error} ({contract})")
            : (new PageRead(result.Page, result.Page.Items.Count, result.Page.Unreadable), result.Page.Next, null);
    }

    private async Task<(int Applied, int Unchanged, int Refused)> ApplyAsync(AmanPullClaim claim, string contract, object page, long next, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<SvcAmanPull>().ApplyAsync(claim, contract, page, next, ct);
            unitOfWork.PromiseToCommit();
            await unitOfWork.EndAsync(ct);
            return outcome;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
