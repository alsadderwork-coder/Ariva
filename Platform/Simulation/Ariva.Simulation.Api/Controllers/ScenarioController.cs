using Ariva.Simulation.Api.Scenarios;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.Simulation.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Simulation.Api.Controllers;

/// <summary>
/// The simulated day (ARV-027): its summary, every queue or one queue at a clock minute, the sensors and the alerts,
/// for operator keys with the read scope; a re-run with another seed for keys with the control scope, rate limited per
/// key and logged with the key's name. Minutes are clock minutes of the demo day, 0 (00:00) to 1439 (23:59).
/// </summary>
[ApiController]
[Route("api/v1/simulation/scenario")]
[Authorize(Policy = SimulationScopes.ReadPolicy)]
public sealed class ScenarioController(ScenarioEngine engine) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ScenarioSummary>(StatusCodes.Status200OK)]
    public ScenarioSummary Get() => engine.Summary();

    /// <summary>Re-runs the reference scenario with another seed (0 to 4294967295).</summary>
    [HttpPut]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [EnableRateLimiting(SimulationScopes.RerunLimit)]
    [ProducesResponseType<ScenarioSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Rerun([FromBody] RerunScenarioRequest request, CancellationToken ct)
    {
        if (request?.Seed is not { } seed || seed is < 0 or > uint.MaxValue)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The seed must be between 0 and 4294967295.");
        return Ok(await engine.RerunAsync((uint)seed, User.Identity?.Name ?? "unknown", ct));
    }

    [HttpGet("queues")]
    [ProducesResponseType<IReadOnlyList<ScenarioQueueState>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Queues([FromQuery, BindRequired] int minute) =>
        ValidMinute(minute) ? Ok(engine.States(minute)) : BadMinute();

    [HttpGet("queues/{queue}")]
    [ProducesResponseType<ScenarioQueueState>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Queue(string queue, [FromQuery, BindRequired] int minute)
    {
        if (!ValidMinute(minute))
            return BadMinute();
        var state = engine.State(queue, minute);
        return state is null ? NotFound() : Ok(state);
    }

    [HttpGet("sensors")]
    [ProducesResponseType<IReadOnlyList<ScenarioSensorState>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Sensors([FromQuery, BindRequired] int minute) =>
        ValidMinute(minute) ? Ok(engine.Sensors(minute)) : BadMinute();

    [HttpGet("alerts")]
    [ProducesResponseType<IReadOnlyList<ScenarioAlert>>(StatusCodes.Status200OK)]
    public IReadOnlyList<ScenarioAlert> Alerts() => engine.Alerts();

    private static bool ValidMinute(int minute) => minute is >= 0 and < ScenarioEngine.Minutes;

    private ObjectResult BadMinute() =>
        Problem(statusCode: StatusCodes.Status400BadRequest, title: "The minute must be a clock minute of the day, 0 to 1439.");
}
