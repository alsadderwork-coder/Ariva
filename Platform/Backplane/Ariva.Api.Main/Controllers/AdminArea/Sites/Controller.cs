using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Sites;

/// <summary>Creating and renaming sites (ARV-012), audited. Sites are never deleted and their codes never change.</summary>
[ApiController]
[Route("api/v1/admin/sites")]
[SiteScoped]
public sealed class AdminSitesController(ISvcSites sites) : ControllerBase
{
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateSite))]
    [ProducesResponseType<SiteViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateSiteRequest request, CancellationToken ct)
    {
        var result = await sites.CreateAsync(request, ct);
        return result.HasErrors
            ? AdministrationProblems.For(this, result.ErrorMessages)
            : Created($"/api/v1/sites/{result.Data.Code}", result.Data);
    }

    [HttpPut("{siteCode}")]
    [Permission(nameof(Global.Defaults.Permissions.EditSite))]
    [ProducesResponseType<SiteViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string siteCode, [FromBody] UpdateSiteRequest request, CancellationToken ct)
    {
        var result = await sites.UpdateAsync(siteCode, request, ct);
        return result.HasErrors ? AdministrationProblems.For(this, result.ErrorMessages) : Ok(result.Data);
    }
}
