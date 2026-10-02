using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Sensing;

/// <summary>
/// The device registry (ARV-021): search, view, register (a critical action that returns the device credential once),
/// change details, move (back to commissioning), rotate the credential (critical, shown once), record and list
/// calibrations (a pass sets the device online), retire (critical), and remove a device that was never calibrated. Limited to the
/// caller's sites (404 outside); every change is audited. Answers that carry a credential are never cached.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class DevicesController(ISvcDevices devices) : ControllerBase
{
    private const string Route = "api/v1/admin/devices";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchDevice))]
    [ProducesResponseType<PageViewModel<DeviceViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] DeviceCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await devices.SearchAsync(criteria, ct));

    /// <summary>The BOQ's assumed coverage footprint for a family and mounting height (a labelled estimate).</summary>
    [HttpGet("assumed-footprint")]
    [Permission(nameof(Global.Defaults.Permissions.SearchDevice))]
    [ProducesResponseType<FootprintViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult AssumedFootprint([FromQuery] string family, [FromQuery] double mountingHeightMetres) =>
        TopologyAnswers.Ok(this, devices.AssumedFootprint(family, mountingHeightMetres));

    /// <summary>The declarative mappings shipped with this version of Ariva (ARV-024), for a device on the declarative dialect.</summary>
    [HttpGet("mappings")]
    [Permission(nameof(Global.Defaults.Permissions.SearchDevice))]
    [ProducesResponseType<IReadOnlyList<DeviceMappingViewModel>>(StatusCodes.Status200OK)]
    public IActionResult Mappings([FromServices] IDeviceMappingCatalog catalog) => Ok(catalog.Mappings);

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewDevice))]
    [ProducesResponseType<DeviceViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await devices.GetAsync(id, ct));

    /// <summary>Registers a device in Commissioning and returns its credential, this once.</summary>
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateDevice))]
    [RequiresRecentMfa]
    [ProducesResponseType<DeviceCredentialViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest request, CancellationToken ct)
    {
        NoStore();
        return TopologyAnswers.Created(this, await devices.RegisterAsync(request, ct), v => v.Device.Id, "/" + Route);
    }

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditDevice))]
    [ProducesResponseType<DeviceViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDeviceRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await devices.UpdateAsync(id, request, ct));

    /// <summary>Moves, re-aims or reassigns the device; it goes back to Commissioning until a calibration passes.</summary>
    [HttpPut("{id:guid}/placement")]
    [Permission(nameof(Global.Defaults.Permissions.EditDevice))]
    [ProducesResponseType<DeviceViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Move(Guid id, [FromBody] DevicePlacement request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await devices.MoveAsync(id, request, ct));

    /// <summary>A new credential, shown this once; the previous one stops working at once.</summary>
    [HttpPost("{id:guid}/credential")]
    [Permission(nameof(Global.Defaults.Permissions.CreateDevice))]
    [RequiresRecentMfa]
    [ProducesResponseType<DeviceCredentialViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RotateCredential(Guid id, CancellationToken ct)
    {
        NoStore();
        return TopologyAnswers.Ok(this, await devices.RotateCredentialAsync(id, ct));
    }

    /// <summary>
    /// Where the device may push from (CIDR blocks; empty for anywhere) and the client certificate it must present
    /// (SHA-256; empty for none). Critical: it decides who can push as this device (ARV-022).
    /// </summary>
    [HttpPut("{id:guid}/access")]
    [Permission(nameof(Global.Defaults.Permissions.EditDevice))]
    [RequiresRecentMfa]
    [ProducesResponseType<DeviceViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetAccess(Guid id, [FromBody] SetDeviceAccessRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await devices.SetAccessAsync(id, request, ct));

    [HttpGet("{id:guid}/calibrations")]
    [Permission(nameof(Global.Defaults.Permissions.ViewDevice))]
    [ProducesResponseType<IReadOnlyList<CalibrationViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Calibrations(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await devices.CalibrationsAsync(id, ct));

    /// <summary>Records a calibration; at or above the threshold (95 percent by default) the device goes Online.</summary>
    [HttpPost("{id:guid}/calibrations")]
    [Permission(nameof(Global.Defaults.Permissions.EditDevice))]
    [ProducesResponseType<CalibrationViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecordCalibration(Guid id, [FromBody] RecordCalibrationRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await devices.RecordCalibrationAsync(id, request, ct), v => v.Id, $"/{Route}/{id}/calibrations");

    /// <summary>Takes the device out of use for good and revokes its credential (critical: it cannot be undone).</summary>
    [HttpPost("{id:guid}/retire")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteDevice))]
    [RequiresRecentMfa]
    [ProducesResponseType<DeviceViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await devices.RetireAsync(id, ct));

    /// <summary>Removes a device registered by mistake; one that was ever calibrated is retired instead (409).</summary>
    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteDevice))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await devices.RemoveAsync(id, ct));

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
