using Ariva.Simulation.Api.Scenarios;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.Simulation.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Simulation.Api.Controllers;

/// <summary>
/// The simulated days (ARV-027; ARV-139b: one per scenario site, <c>site</c> DMO or AUH-TA, DMO when not given): a
/// site's summary, every queue or one queue at a clock minute, the sensors and the alerts, for operator keys with the
/// read scope; a re-run of a site with another seed for keys with the control scope, rate limited per key and logged
/// with the key's name. Minutes are clock minutes of the demo day, 0 (00:00) to 1439 (23:59). A site the simulator does
/// not play is 404, without echoing it.
/// </summary>
[ApiController]
[Route("api/v1/simulation/scenario")]
[Authorize(Policy = SimulationScopes.ReadPolicy)]
public sealed class ScenarioController(ScenarioEngine engine) : ControllerBase
{
    private const int MaxSiteLength = 17;

    [HttpGet]
    [ProducesResponseType<ScenarioSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get([FromQuery] string site = null) =>
        Site(site) is { } code ? Ok(engine.Summary(code)) : UnknownSite();

    /// <summary>The scenario sites the simulator plays and their current days.</summary>
    [HttpGet("sites")]
    [ProducesResponseType<IReadOnlyList<ScenarioSummary>>(StatusCodes.Status200OK)]
    public IReadOnlyList<ScenarioSummary> Sites() => [.. ScenarioEngine.SiteCodes.Select(code => engine.Summary(code))];

    /// <summary>Re-runs a site's scenario (DMO unless the body names another) with another seed (0 to 4294967295).</summary>
    [HttpPut]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [EnableRateLimiting(SimulationScopes.RerunLimit)]
    [ProducesResponseType<ScenarioSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Rerun([FromBody] RerunScenarioRequest request, CancellationToken ct)
    {
        if (request?.Seed is not { } seed || seed is < 0 or > uint.MaxValue)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The seed must be between 0 and 4294967295.");
        if (Site(request.Site) is not { } code)
            return UnknownSite();
        return Ok(await engine.RerunAsync(code, (uint)seed, User.Identity?.Name ?? "unknown", ct));
    }

    [HttpGet("queues")]
    [ProducesResponseType<IReadOnlyList<ScenarioQueueState>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Queues([FromQuery, BindRequired] int minute, [FromQuery] string site = null)
    {
        if (!ValidMinute(minute))
            return BadMinute();
        return Site(site) is { } code ? Ok(engine.States(minute, code)) : UnknownSite();
    }

    [HttpGet("queues/{queue}")]
    [ProducesResponseType<ScenarioQueueState>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Queue(string queue, [FromQuery, BindRequired] int minute, [FromQuery] string site = null)
    {
        if (!ValidMinute(minute))
            return BadMinute();
        if (Site(site) is not { } code)
            return UnknownSite();
        var state = engine.State(queue, minute, code);
        return state is null ? NotFound() : Ok(state);
    }

    [HttpGet("sensors")]
    [ProducesResponseType<IReadOnlyList<ScenarioSensorState>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Sensors([FromQuery, BindRequired] int minute, [FromQuery] string site = null)
    {
        if (!ValidMinute(minute))
            return BadMinute();
        return Site(site) is { } code ? Ok(engine.Sensors(minute, code)) : UnknownSite();
    }

    [HttpGet("alerts")]
    [ProducesResponseType<IReadOnlyList<ScenarioAlert>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Alerts([FromQuery] string site = null) =>
        Site(site) is { } code ? Ok(engine.Alerts(code)) : UnknownSite();

    private static bool ValidMinute(int minute) => minute is >= 0 and < ScenarioEngine.Minutes;

    /// <summary>The site asked for (the reference site when none is), or null when the simulator does not play it (CWE-501: never echoed).</summary>
    private static string Site(string site) =>
        site is null ? ScenarioEngine.ReferenceSite : site.Length <= MaxSiteLength && ScenarioEngine.HasSite(site) ? site : null;

    private ObjectResult BadMinute() =>
        Problem(statusCode: StatusCodes.Status400BadRequest, title: "The minute must be a clock minute of the day, 0 to 1439.");

    private ObjectResult UnknownSite() =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "The simulator plays no scenario for that site.");
}
