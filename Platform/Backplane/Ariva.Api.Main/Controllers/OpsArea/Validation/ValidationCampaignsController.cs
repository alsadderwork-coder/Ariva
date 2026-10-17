using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Validation;

/// <summary>
/// Validation campaigns of a site (ARV-104a, formulas F18, wiki 07 section 8): plan one over the site's published zone profile
/// version (its queue zones, lines and local days), start it, close it, and read campaigns and every observer's manual
/// counts. Reading needs <c>Validation.View</c>, planning and starting <c>Validation.Manage</c>; closing is a critical action
/// (<c>Validation.Manage</c> and a second factor within 15 minutes, security/critical-actions.json) and is audited. A site
/// the caller cannot see answers 404 like one that does not exist, and so does a campaign of another site.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/validation/campaigns")]
[SiteScoped]
public sealed class ValidationCampaignsController(ISvcValidationCampaigns campaigns) : ControllerBase
{
    /// <summary>Campaigns of the site: text in the name, status, created range, an allowlisted sort, pages of at most 500.</summary>
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewValidation))]
    [ProducesResponseType<PageViewModel<ValidationCampaignSummaryViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(string siteCode, [FromQuery] ValidationCampaignCriteria criteria, CancellationToken ct) =>
        ValidationAnswers.Ok(this, await campaigns.SearchAsync(siteCode, criteria, ct));

    /// <summary>Plans a campaign (Planned): its name, the published profile version reviewed, zone and line ids of that version, local days and optional targets.</summary>
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.ManageValidation))]
    [RequestSizeLimit(ValidationAnswers.MaxCampaignBodyBytes)]
    [ProducesResponseType<ValidationCampaignViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Create(string siteCode, [FromBody] CreateValidationCampaignRequest request, CancellationToken ct) =>
        ValidationAnswers.Created(this, await campaigns.CreateAsync(siteCode, request, ct));

    /// <summary>A campaign with its scope, targets, profile version state and the bins captured per line.</summary>
    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewValidation))]
    [ProducesResponseType<ValidationCampaignViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, Guid id, CancellationToken ct) => ValidationAnswers.Ok(this, await campaigns.GetAsync(siteCode, id, ct));

    /// <summary>Starts a planned campaign while its profile version is still the published one (409 otherwise).</summary>
    [HttpPost("{id:guid}/start")]
    [Permission(nameof(Global.Defaults.Permissions.ManageValidation))]
    [ProducesResponseType<ValidationCampaignViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(string siteCode, Guid id, CancellationToken ct) => ValidationAnswers.Ok(this, await campaigns.StartAsync(siteCode, id, ct));

    /// <summary>
    /// Closes a planned or running campaign; nothing is captured or corrected afterwards. Critical: without a second factor in
    /// the last 15 minutes the answer is 401 with <c>insufficient_user_authentication</c> (RFC 9470). Audited.
    /// </summary>
    [HttpPost("{id:guid}/close")]
    [Permission(nameof(Global.Defaults.Permissions.ManageValidation))]
    [RequiresRecentMfa]
    [ProducesResponseType<ValidationCampaignViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Close(string siteCode, Guid id, CancellationToken ct) => ValidationAnswers.Ok(this, await campaigns.CloseAsync(siteCode, id, ct));

    /// <summary>Every observer's counts of the campaign: current revisions (default) or all, by line, observer and bin range (UTC), sorted from an allowlist.</summary>
    [HttpGet("{id:guid}/counts")]
    [Permission(nameof(Global.Defaults.Permissions.ViewValidation))]
    [ProducesResponseType<PageViewModel<ManualCountViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Counts(string siteCode, Guid id, [FromQuery] ManualCountCriteria criteria, CancellationToken ct) =>
        ValidationAnswers.Ok(this, await campaigns.SearchCountsAsync(siteCode, id, criteria, ct));
}
