using System.Reflection;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ariva.UnitTests.Logging.Fixtures;

/// <summary>
/// Fixture for the security event and request enricher tests (ARV-091): an ordinary action that writes one log line naming a
/// staff email, a desk code and a track id, an action whose line names a target account as an administrator's action does,
/// one that writes a line after its answer, and a site-scoped action. Never mounted outside those tests; each action holds a
/// permission like any other.
/// </summary>
/// <param name="logger">The action's logger.</param>
[ApiController]
[Route("fixture/log")]
public sealed class LogProbeController(ILogger<LogProbeController> logger) : ControllerBase
{
    #region Constants

    public const string Email = "probe.staff@example.com";
    public const string DeskCode = "IMM-D99";
    public const string TrackId = "trk-probe-0099";
    public const string Template = "Probe for {Email} at desk {DeskCode}, track {TrackId}";
    public const string AccountTemplate = "Sessions of account {UserId} ended, {SessionId} among them";
    public const string LaterTemplate = "Probe line written after the answer";

    /// <summary>Lets the line of <see cref="Later"/> be written (the test opens it once the answer is back).</summary>
    public static TaskCompletionSource Released { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the line of <see cref="Later"/> is written.</summary>
    public static TaskCompletionSource Written { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    #endregion

    #region Actions

    [HttpGet("ordinary")]
    [Permission(nameof(Global.Defaults.Permissions.ViewSite))]
    public IActionResult Ordinary()
    {
        logger.LogInformation(Template, Email, DeskCode, TrackId);
        return Ok();
    }

    /// <summary>A line shaped like an administrator's action on another account (SvcUsers): the target in {UserId} and {SessionId}.</summary>
    [HttpPost("accounts/{id:guid}/sessions/{sessionId:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.EditUser))]
    public IActionResult Account(Guid id, Guid sessionId)
    {
        logger.LogWarning(AccountTemplate, id, sessionId);
        return NoContent();
    }

    /// <summary>Answers at once and writes a line after the answer, from work the request started.</summary>
    [HttpGet("later")]
    [Permission(nameof(Global.Defaults.Permissions.ViewSite))]
    public IActionResult Later()
    {
        _ = Task.Run(async () =>
        {
            await Released.Task;
            logger.LogInformation(LaterTemplate);
            Written.TrySetResult();
        });
        return Ok();
    }

    [HttpGet("sites/{siteCode}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewSite))]
    [SiteScoped]
    public IActionResult Site(string siteCode) => Ok(new { found = siteCode is not null });

    #endregion

    #region Mounting

    /// <summary>Adds this controller, and no other type of the test assembly, to the host's MVC controllers.</summary>
    public static void Mount(IServiceCollection services) =>
        services.AddMvcCore().ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new Feature()));

    private sealed class Feature : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var probe = typeof(LogProbeController).GetTypeInfo();
            if (!feature.Controllers.Contains(probe))
                feature.Controllers.Add(probe);
        }
    }

    #endregion
}
