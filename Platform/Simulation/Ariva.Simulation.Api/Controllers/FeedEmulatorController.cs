using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Emulators.Aodb;
using Ariva.Simulation.Api.Emulators.Integration;
using Ariva.Simulation.Api.Scenarios;
using Ariva.Simulation.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Simulation.Api.Controllers;

/// <summary>
/// The feed emulators: AMAN, the mock immigration system, the reference site's AODB, and the demo minutes dropped because
/// a feed fell behind the clock; ARV-139b: <see cref="AodbSites"/>, the AODBs of the other scenario sites (AUH-TA).
/// </summary>
public sealed record FeedEmulatorsStatus(ImmigrationFeedStatus Aman, ImmigrationFeedStatus Immigration, AodbStatus Aodb, long ClockMinutesDropped)
{
    public IReadOnlyList<AodbStatus> AodbSites { get; init; } = [];
}

/// <summary>Plays one demo minute (0 to 1439) on the feed emulators now.</summary>
public sealed record PlayMinuteRequest(int? Minute);

/// <summary>
/// The Ariva integration clients of the emulated systems; a missing one is left as it is. <see cref="AodbSites"/> holds
/// the AODB client of other scenario sites by site code (ARV-139b: AUH-TA).
/// </summary>
public sealed record FeedClientsRequest(IntegrationClientSettings Aman, IntegrationClientSettings Immigration, IntegrationClientSettings Aodb)
{
    public IReadOnlyDictionary<string, IntegrationClientSettings> AodbSites { get; init; }
}

/// <summary>One AMAN code and the Ariva desk or e-gate it stands for at the demo airport.</summary>
public sealed record AmanCodeMapping(string AmanCode, string ArivaCode);

/// <summary>
/// The AODB, AMAN and immigration emulators (ARV-029). They play the demo clock of <c>api/v1/simulation/sensors</c>;
/// here: their status and AMAN's codes for read keys; for control keys, playing one minute at once (tests and demos
/// that need a given moment) and the Ariva integration clients they use (issued by Ariva when an administrator
/// registers each system; secrets go in and never come back out). Every control is logged with the key's name.
/// </summary>
[ApiController]
[Route("api/v1/simulation/feeds")]
[Authorize(Policy = SimulationScopes.ReadPolicy)]
public sealed class FeedEmulatorController(BorderFeeds border, AodbEmulators aodbs, FeedClients clients, IEnumerable<IDemoMinuteSink> sinks, TimeProvider time,
    Emulators.Sensors.SensorEmulator clock, ILogger<FeedEmulatorController> logger) : ControllerBase
{
    private string Operator => User.Identity?.Name ?? "unknown";

    [HttpGet]
    [ProducesResponseType<FeedEmulatorsStatus>(StatusCodes.Status200OK)]
    public FeedEmulatorsStatus Get() => new(border.Aman.Status(), border.Immigration.Status(), aodbs.Reference.Status(), clock.FeedMinutesDropped)
    {
        AodbSites = [.. aodbs.Sites.Select(a => a.Status())]
    };

    /// <summary>AMAN's desk and e-gate codes and the Ariva codes they stand for, to set up Ariva's AMAN desk code mappings.</summary>
    [HttpGet("aman-codes")]
    [ProducesResponseType<IReadOnlyList<AmanCodeMapping>>(StatusCodes.Status200OK)]
    public IReadOnlyList<AmanCodeMapping> AmanCodes() => [.. Emulators.Aman.AmanCodes.All().Select(p => new AmanCodeMapping(p.Aman, p.Ariva))];

    [HttpPost("play")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SimulationScopes.PlayLimit)]
    [ProducesResponseType<FeedEmulatorsStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Play([FromBody] PlayMinuteRequest request, CancellationToken ct)
    {
        if (request?.Minute is not { } minute || minute is < 0 or >= ScenarioEngine.Minutes)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The minute must be a clock minute of the day, 0 to 1439.");
        var now = time.GetUtcNow().UtcDateTime;
        logger.LogInformation("Feed emulators play demo minute {Minute} at once, by {Operator}", minute, Operator);
        foreach (var sink in sinks)
            await sink.PlayAsync(minute, _ => now, ct);
        return Ok(Get());
    }

    [HttpPut("clients")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [ProducesResponseType<FeedEmulatorsStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Clients([FromBody] FeedClientsRequest request)
    {
        if (request is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The body is missing.");
        var sites = request.AodbSites ?? new Dictionary<string, IntegrationClientSettings>();
        // CWE-501: a site is named in a message only once it is one of the simulator's other scenario sites.
        if (sites.Count > clients.AodbSites.Count || sites.Keys.Any(site => site is null || !clients.AodbSites.ContainsKey(site)))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "aodbSites holds only scenario sites other than the reference (AUH-TA).");
        var problems = (request.Aman?.Problems("aman") ?? []).Concat(request.Immigration?.Problems("immigration") ?? [])
            .Concat(request.Aodb?.Problems("aodb") ?? [])
            .Concat(sites.Where(p => p.Value is not null).SelectMany(p => p.Value.Problems("aodbSites." + p.Key))).ToList();
        if (problems.Count > 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: string.Join(" ", problems));
        if (request.Aman is not null)
            clients.Aman.Use(request.Aman);
        if (request.Immigration is not null)
            clients.Immigration.Use(request.Immigration);
        if (request.Aodb is not null)
            clients.Aodb.Use(request.Aodb);
        foreach (var (site, credentials) in sites.Where(p => p.Value is not null))
            clients.AodbSites[site].Use(credentials);
        logger.LogInformation("Feed emulator clients replaced by {Operator}: AMAN {Aman}, immigration {Immigration}, AODB {Aodb}, site AODBs {Sites}", Operator,
            request.Aman is not null, request.Immigration is not null, request.Aodb is not null, string.Join(",", sites.Where(p => p.Value is not null).Select(p => p.Key)));
        return Ok(Get());
    }
}
