using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Users;

/// <summary>
/// User administration. ARV-010a and ARV-010b ship the actions an administrator needs for account security (unlock,
/// disable, enable); ARV-011 adds the audited user and role administration around them.
/// </summary>
[ApiController]
[Route("api/v1/admin/users")]
public sealed class UsersController(ISvcAuthenticator authenticator) : ControllerBase
{
    [HttpPost("{id:guid}/unlock")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct) => Answer(await authenticator.UnlockAsync(id, ct));

    /// <summary>Disables the account and ends all its sessions; every host refuses its tokens within 5 seconds.</summary>
    [HttpPost("{id:guid}/disable")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) => Answer(await authenticator.DisableAsync(id, ct));

    [HttpPost("{id:guid}/enable")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) => Answer(await authenticator.EnableAsync(id, ct));

    private IActionResult Answer(Fluentx.Result<bool> result)
    {
        if (!result.HasErrors)
            return NoContent();
        return result.ErrorMessages.Contains(ISvcAuthenticator.CannotChangeOwnAccount)
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not allowed", detail: ISvcAuthenticator.CannotChangeOwnAccount)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }
}
