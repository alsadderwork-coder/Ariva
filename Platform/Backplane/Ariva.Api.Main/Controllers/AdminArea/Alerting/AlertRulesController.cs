using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Alerting;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Alerting;

/// <summary>
/// Alert rules (ARV-037): typed rules per site (metric, comparator, threshold, sustain, clear, severity, owner role,
/// escalation, channels), within the caller's sites and audited. There is no expression field anywhere (CWE-94). Deleting
/// needs a recent second factor.
/// </summary>
[ApiController]
[Route(Route)]
[SiteScoped]
public sealed class AlertRulesController(ISvcAlertRules rules) : ControllerBase
{
    private const string Route = "api/v1/admin/alert-rules";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchAlertRule))]
    [ProducesResponseType<PageViewModel<AlertRuleViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] AlertRuleCriteria criteria, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await rules.SearchAsync(criteria, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAlertRule))]
    [ProducesResponseType<AlertRuleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await rules.GetAsync(id, ct));

    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateAlertRule))]
    [ProducesResponseType<AlertRuleViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] AlertRuleRequest request, CancellationToken ct) =>
        TopologyAnswers.Created(this, await rules.CreateAsync(request, ct), view => view.Id, "/" + Route);

    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditAlertRule))]
    [ProducesResponseType<AlertRuleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] AlertRuleRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await rules.UpdateAsync(id, request, ct));

    /// <summary>Deleting a rule is a critical action (ARV-010d): a second factor within the last 15 minutes.</summary>
    [HttpDelete("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.DeleteAlertRule))]
    [RequiresRecentMfa]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => TopologyAnswers.NoContent(this, await rules.DeleteAsync(id, ct));
}
