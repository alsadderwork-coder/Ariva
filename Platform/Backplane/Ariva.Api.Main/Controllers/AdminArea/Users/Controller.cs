using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Users;

/// <summary>
/// User administration. ARV-010a ships only the unlock action an administrator needs when an account locks;
/// ARV-011 adds the audited user and role administration around it.
/// </summary>
[ApiController]
[Route("api/v1/admin/users")]
public sealed class UsersController(ISvcAuthenticator authenticator) : ControllerBase
{
    [HttpPost("{id:guid}/unlock")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
    {
        var result = await authenticator.UnlockAsync(id, ct);
        return result.HasErrors ? NotFound() : NoContent();
    }
}
