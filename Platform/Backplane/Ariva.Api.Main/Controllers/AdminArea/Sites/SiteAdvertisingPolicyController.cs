using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Sites;

/// <summary>
/// A site's advertising policy (ARV-145, wiki 11 section 10): whether its passenger displays may show advertising in their
/// second region, and the queue zones where it stays off. Off for a site that never had one. The roles that manage displays
/// see it (<c>AdvertisingPolicy.View</c>); changing it is a critical action for system administrators
/// (<c>AdvertisingPolicy.Edit</c> with a second factor in the last 15 minutes), audited with the reason, which names who
/// authorised it. A site the caller cannot see answers 404 like one that does not exist.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/advertising-policy")]
[SiteScoped]
public sealed class SiteAdvertisingPolicyController(ISvcAdvertisingPolicies policies) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertisingPolicy))]
    [ProducesResponseType<AdvertisingPolicyViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await policies.GetAsync(siteCode, ct));

    /// <summary>
    /// Replaces the site's policy: the setting, the excluded zones and the reason, which the audit trail keeps. A change that
    /// allows advertising names the published zone profile it was reviewed against; 409 when another has been published since.
    /// </summary>
    [HttpPut]
    [Permission(nameof(Global.Defaults.Permissions.EditAdvertisingPolicy))]
    [RequiresRecentMfa]
    [ProducesResponseType<AdvertisingPolicyViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Change(string siteCode, [FromBody] AdvertisingPolicyRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await policies.ChangeAsync(siteCode, request, ct));
}
