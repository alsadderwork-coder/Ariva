using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// Zone profiles (ARV-017): the history of a site, a version with its geometry, a draft created from the published
/// version, zone and line edits on the draft, validation, publishing (a critical action: a second factor within 15
/// minutes) and discarding a draft. Published and retired versions never change (409). Limited to the caller's sites
/// (404 outside); every change is audited.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class ZoneProfilesController(ISvcZoneProfiles profiles) : ControllerBase
{
    private const string Route = "api/v1/admin/zone-profiles";

    /// <summary>Every version of the site and its draft, newest first, with who created and published each and when.</summary>
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchZoneProfile))]
    [ProducesResponseType<IReadOnlyList<ZoneProfileSummaryViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> History([FromQuery] string siteCode, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await profiles.HistoryAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewZoneProfile))]
    [ProducesResponseType<ZoneProfileViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await profiles.GetAsync(id, ct));

    /// <summary>A new draft, copied from the site's published version when there is one.</summary>
    [HttpPost("drafts")]
    [Permission(nameof(Global.Defaults.Permissions.CreateZoneProfile))]
    [ProducesResponseType<ZoneProfileViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateDraft(CreateZoneProfileDraftRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await profiles.CreateDraftAsync(request, ct), p => p.Profile.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditZoneProfile))]
    [ProducesResponseType<ZoneProfileViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Rename(Guid id, RenameZoneProfileRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await profiles.RenameAsync(id, request, ct));

    [HttpPost("{id:guid}/zones")]
    [Permission(nameof(Global.Defaults.Permissions.EditZoneProfile))]
    [ProducesResponseType<ZoneViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddZone(Guid id, AddZoneRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await profiles.AddZoneAsync(id, request, ct), z => z.Id, $"/{Route}/{id}/zones");

    [HttpPut("{id:guid}/zones/{zoneId:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditZoneProfile))]
    [ProducesResponseType<ZoneViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateZone(Guid id, Guid zoneId, UpdateZoneRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await profiles.UpdateZoneAsync(id, zoneId, request, ct));

    [HttpDelete("{id:guid}/zones/{zoneId:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditZoneProfile))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveZone(Guid id, Guid zoneId, CancellationToken ct) =>
        TopologyAnswers.NoContent(this, await profiles.RemoveZoneAsync(id, zoneId, ct));

    [HttpPost("{id:guid}/lines")]
    [Permission(nameof(Global.Defaults.Permissions.EditZoneProfile))]
    [ProducesResponseType<LineViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddLine(Guid id, AddLineRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await profiles.AddLineAsync(id, request, ct), l => l.Id, $"/{Route}/{id}/lines");

    [HttpDelete("{id:guid}/lines/{lineId:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditZoneProfile))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveLine(Guid id, Guid lineId, CancellationToken ct) =>
        TopologyAnswers.NoContent(this, await profiles.RemoveLineAsync(id, lineId, ct));

    /// <summary>What stops the draft from being published.</summary>
    [HttpGet("{id:guid}/validation")]
    [Permission(nameof(Global.Defaults.Permissions.ViewZoneProfile))]
    [ProducesResponseType<ZoneProfileValidationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await profiles.ValidateAsync(id, ct));

    /// <summary>
    /// Publishes the draft as the site's next version and retires the one it replaces; ZoneProfilePublished goes out
    /// through the outbox. The body names the geometry hash the publisher reviewed (from the validation); a draft that
    /// changed since answers 409. Critical (security/critical-actions.json): without a second factor in the last 15 minutes
    /// the answer is 401 with <c>insufficient_user_authentication</c> (RFC 9470).
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    [Permission(nameof(Global.Defaults.Permissions.PublishZoneProfile))]
    [RequiresRecentMfa]
    [ProducesResponseType<ZoneProfileSummaryViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(Guid id, PublishZoneProfileRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await profiles.PublishAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteZoneProfile))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Discard(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await profiles.DiscardAsync(id, ct));
}
