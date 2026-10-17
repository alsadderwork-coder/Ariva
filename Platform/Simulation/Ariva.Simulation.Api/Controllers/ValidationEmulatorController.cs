using System.Globalization;
using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Validation;
using Ariva.Simulation.Api.Scenarios;
using Ariva.Simulation.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Simulation.Api.Controllers;

/// <summary>One minute of a queue's truth laid on real time: its UTC start, the people who entered and left in it, and the entrants' waits.</summary>
public sealed record TruthMinute(DateTime StartUtc, int Entries, int Exits, int Waits, double? MeanWaitMinutes);

/// <summary>A queue's truth over the window, minute by minute.</summary>
public sealed record TruthQueue(string Queue, IReadOnlyList<TruthMinute> Minutes);

/// <summary>A desk's states over the window as an observer sees them, one per minute (null outside the scenario day).</summary>
public sealed record TruthDesk(string Desk, string Queue, IReadOnlyList<string> States);

/// <summary>The scenario's truth over a window laid on real time: what Ariva would store if every sensor and feed were perfect.</summary>
public sealed record ValidationTruthAnswer(
    string ScenarioSite,
    uint ScenarioSeed,
    DateTime DayStartUtc,
    int FromMinute,
    int ToMinute,
    IReadOnlyList<TruthQueue> Queues,
    IReadOnlyList<TruthDesk> Desks,
    IReadOnlyList<TruthOutage> Outages);

/// <summary>
/// The validation observer emulator (ARV-104i): for read keys, its status (never an account's user name or a secret) and the
/// scenario's truth over a window laid on real time (to compare with, or to plant a perfect system in a test); for control
/// keys, the observer accounts (user name, password and TOTP seed go in and never come back out) and a rehearsal: the
/// observers sign in to Ariva.Api.Main as normal accounts and send counts, tracer runs and desk logs of the scenario's truth,
/// with the errors asked for, through the capture API (Ariva decides each one). Rehearsals run one at a time, a few a minute per
/// key, and are logged with the key's name. Nothing in a request names where the simulator calls: Ariva's address is
/// configuration (CWE-918). A value that fails a check is never echoed (CWE-501).
/// </summary>
[ApiController]
[Route("api/v1/simulation/validation")]
[Authorize(Policy = SimulationScopes.ReadPolicy)]
public sealed class ValidationEmulatorController(ValidationEmulator emulator, ScenarioEngine engine, ILogger<ValidationEmulatorController> logger) : ControllerBase
{
    private const int MaxRequestBytes = 16 * 1024;
    private const int MaxSiteLength = 17;
    private string Operator => User.Identity?.Name ?? "unknown";

    [HttpGet]
    [ProducesResponseType<ValidationEmulatorStatus>(StatusCodes.Status200OK)]
    public ValidationEmulatorStatus Get() => emulator.Status();

    /// <summary>Replaces the observer accounts (1 to 8): the sessions of the accounts replaced are signed out.</summary>
    [HttpPut("observers")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ValidationEmulatorStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Observers([FromBody] ObserversRequest request, CancellationToken ct)
    {
        var problems = ObserverCredentials.ListProblems(request?.Observers, "observer", allowEmpty: false).Take(10).ToList();
        if (problems.Count > 0)
            return Bad(string.Join(" ", problems));
        if (!await emulator.UseObserversAsync(request.Observers, Operator, ct))
            return Refused("A rehearsal is running; replace the observers when it has finished.");
        return Ok(emulator.Status());
    }

    /// <summary>Runs a rehearsal now and answers its report (200), or why it could not start (409).</summary>
    [HttpPost("rehearsals")]
    [Authorize(Policy = SimulationScopes.ControlPolicy)]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<RehearsalReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Rehearse([FromBody] RehearsalRequest request, CancellationToken ct)
    {
        var (rehearsal, problem) = Check(request);
        if (problem is not null)
            return Bad(problem);
        var (refused, report) = await emulator.RehearseAsync(rehearsal, Operator, ct);
        return refused switch
        {
            RehearsalRefused.Busy => Refused("A rehearsal is running; one runs at a time."),
            RehearsalRefused.NoArivaAddress => Refused("The simulator has no address of Ariva.Api.Main (Simulation:Ariva:MainUrl)."),
            RehearsalRefused.NoObservers => Refused("No observer account is configured (PUT api/v1/simulation/validation/observers or Simulation:Validation:Observers)."),
            RehearsalRefused.OtherSeed => Refused("The scenario site's day runs with another seed; re-run it with the seed the rehearsal names, or leave scenarioSeed out."),
            RehearsalRefused.RateLimited => TooMany(),
            _ => Ok(report)
        };
    }

    /// <summary>
    /// The scenario's truth over clock minutes [fromMinute, toMinute) laid on <c>dayStartUtc</c>: per queue (all, or those in
    /// <c>queues</c>, comma separated) each minute's entries, exits and entrants' mean wait; each server's observed state; the
    /// sensor outages. All values are text so that none fails to bind (and so is never echoed). A few reads a minute per key
    /// (<see cref="ValidationEmulatorSettings.TruthReadsPerMinute"/>; 429 with Retry-After beyond).
    /// </summary>
    [HttpGet("truth")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SimulationScopes.TruthLimit)]
    [ProducesResponseType<ValidationTruthAnswer>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult Truth([FromQuery] string site, [FromQuery] string dayStartUtc, [FromQuery] string fromMinute, [FromQuery] string toMinute,
        [FromQuery] string queues)
    {
        var scenarioSite = ScenarioSite(site);
        if (scenarioSite is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "The simulator plays no scenario for that site.");
        if (DayStart(dayStartUtc) is not { } dayStart)
            return Bad(DayStartRule);
        if (!int.TryParse(fromMinute, NumberStyles.None, CultureInfo.InvariantCulture, out var from) ||
            !int.TryParse(toMinute, NumberStyles.None, CultureInfo.InvariantCulture, out var to) || from is < 0 or >= ScenarioEngine.Minutes ||
            to <= from || to > ScenarioEngine.Minutes + ValidationEmulatorSettings.MaxTruthMinutes || to - from > ValidationEmulatorSettings.MaxTruthMinutes)
            return Bad("fromMinute is a clock minute of the day (0 to 1439) and toMinute a later one, at most 360 minutes on.");
        var names = string.IsNullOrEmpty(queues) ? null : queues.Split(',', StringSplitOptions.TrimEntries);
        if (names is not null && (names.Length > 32 || names.Any(n => n.Length is 0 or > 32)))
            return Bad("queues names at most 32 queues of the scenario, separated by commas.");

        return Ok(engine.Read(scenarioSite, day =>
        {
            var truth = new ValidationTruth(day, dayStart);
            var fromUtc = truth.At(from);
            var toUtc = truth.At(to);
            var picked = Enumerable.Range(0, day.Site.NQ).Where(q => names is null || names.Contains(day.Site.Queues[q].Id, StringComparer.Ordinal)).ToList();
            var minutes = Enumerable.Range(from, to - from).Select(m => truth.At(m)).ToList();
            var answer = new ValidationTruthAnswer(day.Site.Code, day.Seed, dayStart, from, to,
                [.. picked.Select(q => new TruthQueue(day.Site.Queues[q].Id, [.. minutes.Select(m =>
                {
                    var waits = truth.EntrantWaits(q, m);
                    return new TruthMinute(m, waits.Entries, truth.Exits(q, m, m.AddMinutes(1)).Count, waits.Waits, waits.MeanWaitMinutes);
                })]))],
                [.. picked.SelectMany(q => day.Site.Queues[q].Servers.Select((server, k) => new TruthDesk(server, day.Site.Queues[q].Id,
                    [.. minutes.Select(m => truth.DeskState(q, k, m)?.ToString())])))],
                [.. truth.Outages(fromUtc, toUtc).Where(o => picked.Any(q => string.Equals(day.Site.Queues[q].Id, o.QueueZone, StringComparison.Ordinal)))]);
            return answer;
        }));
    }

    #region Checks

    private const string DayStartRule = "dayStartUtc is the UTC instant of the scenario's 00:00, ISO 8601 ending in Z, from 2000 to 2099.";

    /// <summary>The request as the emulator runs it, or the first rule it breaks (never quoting a value).</summary>
    private static (Rehearsal Rehearsal, string Problem) Check(RehearsalRequest request)
    {
        if (request is null)
            return (null, "The body is missing.");
        if (request.SiteCode is null || request.SiteCode.Length > MaxSiteLength || !FeedRules.Code().IsMatch(request.SiteCode))
            return (null, "siteCode is the Ariva site of the campaign (for example DMO).");
        if (request.CampaignId is not { } campaign || campaign == Guid.Empty)
            return (null, "campaignId is the id of a running campaign at that site.");
        var scenarioSite = request.ScenarioSite is null ? ScenarioEngine.ReferenceSite : ScenarioSite(request.ScenarioSite);
        if (scenarioSite is null)
            return (null, "scenarioSite is a site the simulator plays (DMO or AUH-TA).");
        if (request.ScenarioSeed is < 0 or > uint.MaxValue)
            return (null, "scenarioSeed is from 0 to 4294967295.");
        if (DayStart(request.DayStartUtc) is not { } dayStart)
            return (null, DayStartRule);
        if (request.FromMinute is not { } from || request.ToMinute is not { } to || from is < 0 or >= ScenarioEngine.Minutes || to <= from ||
            to > ScenarioEngine.Minutes || to - from > ValidationEmulatorSettings.MaxWindowMinutes)
            return (null, "fromMinute and toMinute are clock minutes of the day (0 to 1440), toMinute later, at most 240 minutes on.");
        var errors = new ObserverErrors(request.CountErrorPercent ?? 0, request.CountErrorLines, request.TracerErrorMinutes ?? 0, request.TracerErrorPercent ?? 0,
            request.MissedBinsPercent ?? 0, request.MissedMinutesPercent ?? 0);
        var problems = errors.Problems().ToList();
        if (problems.Count > 0)
            return (null, string.Join(" ", problems));
        if (request.TracerEveryMinutes is < 1 or > 60)
            return (null, "tracerEveryMinutes is from 1 to 60.");
        if (request.Seed is < 0 or > uint.MaxValue)
            return (null, "seed is from 0 to 4294967295.");
        return (new Rehearsal(request.SiteCode, campaign, scenarioSite, request.ScenarioSeed is { } s ? (uint)s : null, dayStart, new RehearsalWindow(from, to), errors,
            request.TracerEveryMinutes ?? 10, request.Seed is { } seed ? (uint)seed : null), null);
    }

    /// <summary>A scenario site the simulator plays (DMO when none is named), or null (never echoed).</summary>
    private static string ScenarioSite(string site) =>
        site is null ? ScenarioEngine.ReferenceSite : site.Length <= MaxSiteLength && ScenarioEngine.HasSite(site) ? site : null;

    /// <summary>A UTC instant in ISO 8601 ending in Z, from 2000 to 2099, cut to the millisecond; null otherwise.</summary>
    private static DateTime? DayStart(string text)
    {
        if (text is not { Length: >= 11 and <= 40 } || !text.EndsWith('Z') ||
            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ||
            parsed.Kind != DateTimeKind.Utc || parsed.Year is < 2000 or > 2099)
            return null;
        return new DateTime(parsed.Ticks - (parsed.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    private ObjectResult Bad(string title)
    {
        logger.LogInformation("Validation emulator request refused for {Operator}", Operator);
        return Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
    }

    private ObjectResult TooMany()
    {
        Response.Headers.RetryAfter = "60";
        return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "This key started its rehearsals of this minute already; try again in a minute.");
    }

    private ObjectResult Refused(string title) => Problem(statusCode: StatusCodes.Status409Conflict, title: title);

    #endregion
}
