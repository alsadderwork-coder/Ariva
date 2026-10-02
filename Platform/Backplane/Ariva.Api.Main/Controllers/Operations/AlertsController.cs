using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Alerting;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.Operations;

/// <summary>
/// Alerts (ARV-039): the operational view of what the rules raised. Only the alerts of the caller's sites that the
/// caller's role is responsible for are visible; acknowledging, escalating and resolving move an alert forward only
/// (409 otherwise), are audited and are pushed to the live hub.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class AlertsController(ISvcAlerts alerts) : ControllerBase
{
    private const string Route = "api/v1/alerts";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchAlert))]
    [ProducesResponseType<PageViewModel<AlertViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] AlertCriteria criteria, CancellationToken ct) => TopologyAnswers.Ok(this, await alerts.SearchAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAlert))]
    [ProducesResponseType<AlertViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await alerts.GetAsync(id, ct));

    /// <summary>Takes the alert on (from Raised or Escalated), with an optional note.</summary>
    [HttpPost("{id:guid}/acknowledge")]
    [Permission(nameof(Global.Defaults.Permissions.EditAlert))]
    [ProducesResponseType<AlertViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Acknowledge(Guid id, [FromBody] AlertActionRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await alerts.AcknowledgeAsync(id, request, ct));

    /// <summary>Escalates the alert now to its rule's escalation role or contact (from Raised or Acknowledged, once).</summary>
    [HttpPost("{id:guid}/escalate")]
    [Permission(nameof(Global.Defaults.Permissions.EditAlert))]
    [ProducesResponseType<AlertViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Escalate(Guid id, [FromBody] AlertActionRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await alerts.EscalateAsync(id, request, ct));

    /// <summary>Resolves the alert by hand, with a note saying why.</summary>
    [HttpPost("{id:guid}/resolve")]
    [Permission(nameof(Global.Defaults.Permissions.EditAlert))]
    [ProducesResponseType<AlertViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] AlertActionRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await alerts.ResolveAsync(id, request, ct));
}
