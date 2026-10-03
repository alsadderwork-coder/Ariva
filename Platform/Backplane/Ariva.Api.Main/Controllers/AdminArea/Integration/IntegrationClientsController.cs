using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Integration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Integration;

/// <summary>
/// Integration clients (ARV-042): list and view; register (returns the client id, secret and TOTP seed once), change what
/// a client may do, rotate its secret or reset its seed (each shown once), disable, enable and unlock. Every change is a
/// critical action (a second factor in the last 15 minutes) and is audited. A client is visible only to administrators
/// whose sites cover all of its sites (404 otherwise). Answers that carry a secret are never cached.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class IntegrationClientsController(ISvcIntegrationClients clients) : ControllerBase
{
    private const string Route = "api/v1/admin/integration-clients";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchIntegrationClient))]
    [ProducesResponseType<IReadOnlyList<IntegrationClientViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string siteCode, CancellationToken ct) => Answer(await clients.ListAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewIntegrationClient))]
    [ProducesResponseType<IntegrationClientViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Answer(await clients.GetAsync(id, ct));

    /// <summary>Registers a client and returns its client id, secret and TOTP seed, this once.</summary>
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<IntegrationClientCredentialsViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateIntegrationClientRequest request, CancellationToken ct)
    {
        NoStore();
        var result = await clients.CreateAsync(request, ct);
        return result.HasErrors ? Answer(result) : Created($"/{Route}/{result.Data.Client.Id}", result.Data);
    }

    /// <summary>Changes the name, scopes, sites, networks and per-request TOTP policy; the client's tokens stop working.</summary>
    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<IntegrationClientViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateIntegrationClientRequest request, CancellationToken ct) =>
        Answer(await clients.UpdateAsync(id, request, ct));

    /// <summary>A new client secret, shown this once; the previous one and the client's tokens stop working at once.</summary>
    [HttpPost("{id:guid}/secret")]
    [Permission(nameof(Global.Defaults.Permissions.CreateIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<IntegrationClientCredentialsViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateSecret(Guid id, CancellationToken ct)
    {
        NoStore();
        return Answer(await clients.RotateSecretAsync(id, ct));
    }

    /// <summary>A new TOTP seed, shown this once with its otpauth URI; the previous one and the client's tokens stop working.</summary>
    [HttpPost("{id:guid}/totp")]
    [Permission(nameof(Global.Defaults.Permissions.CreateIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<IntegrationClientCredentialsViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetTotp(Guid id, CancellationToken ct)
    {
        NoStore();
        return Answer(await clients.ResetTotpAsync(id, ct));
    }

    [HttpPost("{id:guid}/disable")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<IntegrationClientViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) => Answer(await clients.DisableAsync(id, ct));

    [HttpPost("{id:guid}/enable")]
    [Permission(nameof(Global.Defaults.Permissions.EditIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<IntegrationClientViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) => Answer(await clients.EnableAsync(id, ct));

    /// <summary>Clears a lockout after repeated failed token exchanges (find the cause first: runbook, integration client locked out).</summary>
    [HttpPost("{id:guid}/unlock")]
    [Permission(nameof(Global.Defaults.Permissions.EditIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<IntegrationClientViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct) => Answer(await clients.UnlockAsync(id, ct));

    private IActionResult Answer<T>(Fluentx.Result<T> result)
    {
        if (!result.HasErrors)
            return Ok(result.Data);
        var first = result.ErrorMessages?.FirstOrDefault() ?? IntegrationErrors.NotFound;
        var (status, title) = first switch
        {
            IntegrationErrors.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            IntegrationErrors.BeyondOwnSites => (StatusCodes.Status403Forbidden, "Not allowed"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        var problem = Problem(statusCode: status, title: title, detail: status == StatusCodes.Status404NotFound ? null : first);
        if (result.ErrorMessages.Count > 1 && status == StatusCodes.Status400BadRequest && problem.Value is ProblemDetails details)
            details.Extensions["errors"] = result.ErrorMessages;
        return problem;
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
