using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Roles;

/// <summary>The four fixed roles with their rank and permissions (ARV-011). Roles are granted on the users endpoints.</summary>
[ApiController]
[Route("api/v1/admin/roles")]
public sealed class RolesController(ISvcRoleAssignment roles) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchRole))]
    [ProducesResponseType<IReadOnlyList<RoleViewModel>>(StatusCodes.Status200OK)]
    public IActionResult List() => Ok(roles.Roles());
}
