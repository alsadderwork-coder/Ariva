using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios;
using Ariva.Simulation.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Controllers;

/// <summary>Starts the demo clock: optionally at a minute (0 to 1439), at a speed and until a minute.</summary>
public sealed record StartSensorsRequest(int? Minute, double? Speed, int? UntilMinute);

/// <summary>Sets the speed in demo minutes per wall minute.</summary>
public sealed record SpeedRequest(double? Speed);

/// <summary>Jumps to a demo minute (0 to 1439).</summary>
public sealed record JumpRequest(int? Minute);

/// <summary>Replaces the emulated devices.</summary>
public sealed record SensorDevicesRequest(IReadOnlyList<EmulatedDeviceSettings> Devices);

/// <summary>
/// The sensor emulator (ARV-028): its status for read keys; start, pause, speed, jump and the device list for control
/// keys (the seed is the scenario's, <c>PUT api/v1/simulation/scenario</c>). Device credentials go in and never come
/// back out. Every control is logged with the operator key's name.
/// </summary>
[ApiController]
[Route("api/v1/simulation/sensors")]
[Authorize(Policy = SimulationScopes.ReadPolicy)]
public sealed class SensorEmulatorController(SensorEmulator emulator, IOptionsMonitor<SensorEmulatorSettings> settings) : ControllerBase
{
    private string Operator => User.Identity?.Name ?? "unknown";

    [HttpGet]
    [ProducesResponseType<SensorEmulatorStatus>(StatusCodes.Status200OK)]
    public SensorEmulatorStatus Get() => emulator.Status();

    [HttpPost("start")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [ProducesResponseType<SensorEmulatorStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Start([FromBody] StartSensorsRequest request)
    {
        if (request is null)
            return Bad("The body is missing.");
        if (request.Minute is { } m && !ValidMinute(m))
            return Bad("The minute must be a clock minute of the day, 0 to 1439.");
        if (request.Speed is { } s && !ValidSpeed(s))
            return Bad(SpeedRule());
        if (request.UntilMinute is { } u && (u is < 1 or > ScenarioEngine.Minutes || (request.Minute is { } from && u <= from)))
            return Bad("The stop minute must be after the start minute and at most 1440.");
        return Ok(emulator.Start(request.Minute, request.Speed, request.UntilMinute, Operator));
    }

    [HttpPost("pause")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [ProducesResponseType<SensorEmulatorStatus>(StatusCodes.Status200OK)]
    public SensorEmulatorStatus Pause() => emulator.Pause(Operator);

    [HttpPut("speed")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [ProducesResponseType<SensorEmulatorStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Speed([FromBody] SpeedRequest request) =>
        request?.Speed is { } s && ValidSpeed(s) ? Ok(emulator.SetSpeed(s, Operator)) : Bad(SpeedRule());

    [HttpPost("jump")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [ProducesResponseType<SensorEmulatorStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Jump([FromBody] JumpRequest request) =>
        request?.Minute is { } m && ValidMinute(m) ? Ok(emulator.Jump(m, Operator)) : Bad("The minute must be a clock minute of the day, 0 to 1439.");

    /// <summary>Replaces the devices the emulator plays: scenario sensor, dialect and the credential Ariva issued.</summary>
    [HttpPut("devices")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [ProducesResponseType<SensorEmulatorStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Devices([FromBody] SensorDevicesRequest request)
    {
        if (request?.Devices is null)
            return Bad("The body needs a devices list.");
        var problems = SensorEmulatorSettings.DeviceProblems(request.Devices).Take(10).ToList();
        return problems.Count > 0 ? Bad(string.Join(" ", problems)) : Ok(emulator.SetDevices(request.Devices, Operator));
    }

    private static bool ValidMinute(int minute) => minute is >= 0 and < ScenarioEngine.Minutes;

    private bool ValidSpeed(double speed) => double.IsFinite(speed) && speed >= 0.5 && speed <= settings.CurrentValue.MaxSpeed;

    private string SpeedRule() => FormattableString.Invariant($"The speed is from 0.5 to {settings.CurrentValue.MaxSpeed} demo minutes per wall minute.");

    private ObjectResult Bad(string title) => Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
