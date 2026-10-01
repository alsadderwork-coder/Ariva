using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Users;

/// <summary>
/// User administration (ARV-010a, ARV-010b, ARV-011). Every change is audited. Creating a user, resetting a password
/// or an authenticator, and granting or revoking a role are critical actions: they need a second factor within
/// 15 minutes ([RequiresRecentMfa], security/critical-actions.json). The break-glass account is not visible here.
/// </summary>
[ApiController]
[Route("api/v1/admin/users")]
public sealed class UsersController(ISvcAuthenticator authenticator, ISvcUsers users, ISvcRoleAssignment roles) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchUser))]
    [ProducesResponseType<PageViewModel<UserViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] UserCriteria criteria, CancellationToken ct) => Answer(await users.SearchAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewUser))]
    [ProducesResponseType<UserViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Answer(await users.GetAsync(id, ct));

    /// <summary>Creates the account with a temporary password, shown once in the response.</summary>
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateUser))]
    [RequiresRecentMfa]
    [ProducesResponseType<UserCreatedViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await users.CreateAsync(request, ct);
        return result.HasErrors ? Problem(result.ErrorMessages) : CreatedAtAction(nameof(Get), new { id = result.Data.User.Id }, result.Data);
    }

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [ProducesResponseType<UserViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct) => Answer(await users.UpdateAsync(id, request, ct));

    /// <summary>A new temporary password, shown once; the user's sessions end.</summary>
    [HttpPost("{id:guid}/reset-password")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [RequiresRecentMfa]
    [ProducesResponseType<TemporaryPasswordViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Answer(await users.ResetPasswordAsync(id, ct));
    }

    /// <summary>Clears the authenticator and recovery codes; the user enrols again and its sessions end.</summary>
    [HttpPost("{id:guid}/reset-totp")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [RequiresRecentMfa]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetTotp(Guid id, CancellationToken ct)
    {
        var result = await users.ResetTotpAsync(id, ct);
        return result.HasErrors ? Problem(result.ErrorMessages) : NoContent();
    }

    [HttpPut("{id:guid}/roles/{roleCode:alpha}")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [RequiresRecentMfa]
    [ProducesResponseType<UserViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Grant(Guid id, string roleCode, CancellationToken ct) => Answer(await roles.GrantAsync(id, roleCode, ct));

    [HttpDelete("{id:guid}/roles/{roleCode:alpha}")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [RequiresRecentMfa]
    [ProducesResponseType<UserViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id, string roleCode, CancellationToken ct) => Answer(await roles.RevokeAsync(id, roleCode, ct));

    /// <summary>Replaces the user's site access (every site, or a list); only within the caller's own sites.</summary>
    [HttpPut("{id:guid}/sites")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    [RequiresRecentMfa]
    [SiteScoped]
    [ProducesResponseType<UserViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetSites(Guid id, [FromBody] SiteAccessRequest request, CancellationToken ct) => Answer(await users.SetSitesAsync(id, request, ct));

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
        var error = result.ErrorMessages.FirstOrDefault();
        return error is ISvcAuthenticator.CannotChangeOwnAccount or ISvcAuthenticator.LastAdministrator
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not allowed", detail: error)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }

    private IActionResult Answer<T>(Fluentx.Result<T> result) => result.HasErrors ? Problem(result.ErrorMessages) : Ok(result.Data);

    private ObjectResult Problem(IEnumerable<string> errors) => AdministrationProblems.For(this, errors);
}
