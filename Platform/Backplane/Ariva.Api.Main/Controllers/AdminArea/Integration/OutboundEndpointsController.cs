using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Integration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Integration;

/// <summary>
/// Outbound endpoints (ARV-045): the systems Ariva calls. List and view (never the secret); register with the secret
/// material, change the connection, replace the secret, disable and enable. Every change is a critical action (a
/// second factor in the last 15 minutes) and is audited. Administered with the integration permissions (System
/// administrator); an endpoint is visible only to administrators whose sites cover all of its sites (404 otherwise).
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class OutboundEndpointsController(ISvcOutboundEndpoints endpoints) : ControllerBase
{
    private const string Route = "api/v1/admin/outbound-endpoints";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchIntegrationClient))]
    [ProducesResponseType<IReadOnlyList<OutboundEndpointViewModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string siteCode, CancellationToken ct) => Answer(await endpoints.ListAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewIntegrationClient))]
    [ProducesResponseType<OutboundEndpointViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Answer(await endpoints.GetAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<OutboundEndpointViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateOutboundEndpointRequest request, CancellationToken ct)
    {
        var result = await endpoints.CreateAsync(request, ct);
        return result.HasErrors ? Answer(result) : Created($"/{Route}/{result.Data.Id}", result.Data);
    }

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<OutboundEndpointViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateOutboundEndpointRequest request, CancellationToken ct) =>
        Answer(await endpoints.UpdateAsync(id, request, ct));

    /// <summary>Replaces the secret material (write-only); calls use it from the next request.</summary>
    [HttpPut("{id:guid}/secret")]
    [Permission(nameof(Global.Defaults.Permissions.CreateIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<OutboundEndpointViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetSecret(Guid id, [FromBody] OutboundSecretRequest request, CancellationToken ct) =>
        Answer(await endpoints.SetSecretAsync(id, request, ct));

    [HttpPost("{id:guid}/disable")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<OutboundEndpointViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) => Answer(await endpoints.DisableAsync(id, ct));

    [HttpPost("{id:guid}/enable")]
    [Permission(nameof(Global.Defaults.Permissions.EditIntegrationClient))]
    [RequiresRecentMfa]
    [ProducesResponseType<OutboundEndpointViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) => Answer(await endpoints.EnableAsync(id, ct));

    private IActionResult Answer<T>(Fluentx.Result<T> result)
    {
        if (!result.HasErrors)
            return Ok(result.Data);
        var first = result.ErrorMessages?.FirstOrDefault() ?? OutboundErrors.NotFound;
        var (status, title) = first switch
        {
            OutboundErrors.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            OutboundErrors.BeyondOwnSites => (StatusCodes.Status403Forbidden, "Not allowed"),
            OutboundErrors.DuplicateCode => (StatusCodes.Status409Conflict, "Conflict"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        var problem = Problem(statusCode: status, title: title, detail: status == StatusCodes.Status404NotFound ? null : first);
        if (result.ErrorMessages.Count > 1 && status == StatusCodes.Status400BadRequest && problem.Value is ProblemDetails details)
            details.Extensions["errors"] = result.ErrorMessages;
        return problem;
    }
}
