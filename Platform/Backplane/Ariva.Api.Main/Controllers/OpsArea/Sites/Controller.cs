using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Sites;

/// <summary>The caller's sites (ARV-012): another site answers 404, exactly like a site that does not exist.</summary>
[ApiController]
[Route("api/v1/sites")]
[SiteScoped]
public sealed class SitesController(ISvcSites sites) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchSite))]
    [ProducesResponseType<IReadOnlyList<SiteViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok((await sites.ListAsync(ct)).Data);

    [HttpGet("{siteCode}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewSite))]
    [ProducesResponseType<SiteViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, CancellationToken ct)
    {
        var result = await sites.GetAsync(siteCode, ct);
        return result.HasErrors ? AdministrationProblems.For(this, result.ErrorMessages) : Ok(result.Data);
    }
}
