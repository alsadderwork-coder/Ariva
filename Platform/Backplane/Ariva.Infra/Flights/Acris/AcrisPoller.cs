using System.Net;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
using Ariva.Core.Services;
using Ariva.Infra.Integration.Outbound;
using Ariva.Infra.Resilience;
using Ariva.Infra.Services.Flights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ariva.Infra.Flights.Acris;

/// <summary>
/// Pulls ACRIS flights (ARV-045, wiki 08 section 9) from every active AcrisFlights endpoint when it is due: claims it
/// (one replica per endpoint), sends <c>GET</c> its pull path through the endpoint's guarded client with
/// <c>If-Modified-Since</c>, reads at most 10 MB and 5,000 flights, maps them for the endpoint's site and applies them in
/// units of 500 as the feed <c>acris-</c> and the endpoint's code, then records the outcome on the endpoint (status, the
/// answer's Last-Modified, failures in a row). A refused call (an address outside the endpoint's networks, a redirect) is
/// recorded and logged as a warning; nothing is retried that the endpoint's policies do not retry. A round ends at
/// <see cref="RunTimeout"/> (ARV-096e): the units already applied stay, a pull it cut is recorded on its endpoint as a failure
/// (its Last-Modified unchanged, so the next pull asks for the whole answer again), and the next poll tries again.
/// </summary>
internal sealed class AcrisPoller(IServiceScopeFactory scopes, OutboundClients clients, TimeProvider time, Microsoft.Extensions.Configuration.IConfiguration configuration,
    ILogger<AcrisPoller> logger) : BackgroundService, IBoundedRuns
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private const int ClaimsPerTick = 4;

    /// <summary>
    /// Five minutes: four endpoints at once, each one call within its own call timeout, at most 10 MB and 5,000 flights applied
    /// in units of 500. A slower partner is cut here and asked again at its next poll.
    /// </summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Integration:Outbound:PollAcris false turns the pull off in this host (tests that start the host without a database).
        if (!Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(configuration, "Integration:Outbound:PollAcris", true))
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
                logger.LogError(e, "ACRIS pull round failed; retrying at the next tick");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One round under the run timeout: claim due endpoints and pull each. Public for tests.</summary>
    public Task<int> RunOnceAsync(CancellationToken stoppingToken) =>
        RunBound.RunAsync("ACRIS pull", RunTimeout, time, ct => RoundAsync(ct, stoppingToken), stoppingToken);

    private async Task<int> RoundAsync(CancellationToken ct, CancellationToken stoppingToken)
    {
        IReadOnlyList<AcrisPullClaim> claims;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claims = await scope.ServiceProvider.GetRequiredService<SvcAcrisPull>().ClaimDueAsync(ClaimsPerTick, ct);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(ct);
        }

        // At most four at once (the claim's limit), so one slow endpoint does not hold the others back.
        await Task.WhenAll(claims.Select(claim => PullAsync(claim, ct, stoppingToken)));
        return claims.Count;
    }

    private async Task PullAsync(AcrisPullClaim claim, CancellationToken ct, CancellationToken stoppingToken)
    {
        string status;
        var success = false;
        string lastModified = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, claim.PullPath.TrimStart('/'));
            if (DateTimeOffset.TryParse(claim.LastModified, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var since))
                request.Headers.IfModifiedSince = since;
            using var response = await clients.For(claim.Target).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                (success, status) = (true, "Not modified.");
            }
            else if (!response.IsSuccessStatusCode)
            {
                status = $"The endpoint answered {(int)response.StatusCode}.";
            }
            else
            {
                var body = await response.Content.ReadAsByteArrayAsync(ct);
                var (flights, error) = AcrisReader.Read(body);
                if (error is not null)
                {
                    status = error;
                }
                else
                {
                    var modified = response.Content.Headers.LastModified;
                    var (applied, refused) = await ApplyAsync(claim, flights, modified?.UtcDateTime, ct);
                    (success, status) = (true, $"Pulled {flights.Count} flights: {applied} applied, {refused} refused.");
                    lastModified = modified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        catch (OutboundRefusedException e)
        {
            status = e.Message;
            logger.LogWarning("ACRIS pull from {Endpoint} refused: {Reason}", claim.Target.Code, e.Message);
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            // A secret protected with a key the ring no longer has (or another ring's): this endpoint fails, the others still pull.
            status = "The endpoint's secret cannot be read with this key ring; set the secret again.";
            logger.LogError(e, "ACRIS pull from {Endpoint}: its secret cannot be unprotected", claim.Target.Code);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            status = e is OutboundUnavailableException ? e.Message : "The endpoint could not be reached or did not answer in time.";
            logger.LogWarning(e, "ACRIS pull from {Endpoint} failed", claim.Target.Code);
        }
#pragma warning disable CA1031 // the round's run timeout (ARV-096e) cut it: recorded as a failure, the round then reports the cut
        catch (Exception) when (ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            (success, lastModified) = (false, null);
            status = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The pull ran past the poll's run timeout of {RunTimeout.TotalSeconds:0} s and was cut; the next poll asks again.");
            logger.LogWarning("ACRIS pull from {Endpoint} was cut by the poll's run timeout", claim.Target.Code);
        }

        // Recorded with the host's token: a cut pull is still recorded (each statement within the database command timeout).
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SvcAcrisPull>().RecordAsync(claim.Target.Id, success, status, lastModified, stoppingToken);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(stoppingToken);
    }

    private async Task<(int Applied, int Refused)> ApplyAsync(AcrisPullClaim claim, IReadOnlyList<AcrisFlight> flights, DateTime? modifiedUtc, CancellationToken ct)
    {
        IReadOnlySet<string> airports;
        await using (var scope = scopes.CreateAsyncScope())
            airports = await scope.ServiceProvider.GetRequiredService<SvcAcrisPull>().AirportsAsync(claim.SiteCode, ct);

        var legs = new List<FlightLegData>();
        var refused = 0;
        foreach (var flight in flights)
        {
            var (leg, error) = AcrisMapping.ToLeg(flight, airports);
            if (error is null)
                legs.Add(leg);
            else
                refused++;
        }

        var now = time.GetUtcNow().UtcDateTime;
        DateTime? source = modifiedUtc is { } m && FlightRules.IsPlausibleSource(DateTime.SpecifyKind(m, DateTimeKind.Utc), now) ? DateTime.SpecifyKind(m, DateTimeKind.Utc) : null;
        var applied = 0;
        foreach (var chunk in System.Linq.Enumerable.Chunk(legs, FlightRules.MaxBatch))
        {
            await using var scope = scopes.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            IReadOnlyList<FlightItemResult> results;
            try
            {
                results = await scope.ServiceProvider.GetRequiredService<SvcAcrisPull>().ApplyAsync(claim.SiteCode, "acris-" + claim.Target.Code, chunk, source, ct);
                unitOfWork.PromiseToCommit();
                await unitOfWork.EndAsync(ct);
            }
            catch
            {
                // Disposing a unit that was never ended commits what was promised: a unit that failed part way is rolled back.
                await unitOfWork.RollbackAsync(CancellationToken.None);
                throw;
            }

            applied += results.Count(r => r.Applied);
            refused += results.Count(r => r.HasErrors);
        }

        return (applied, refused);
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
