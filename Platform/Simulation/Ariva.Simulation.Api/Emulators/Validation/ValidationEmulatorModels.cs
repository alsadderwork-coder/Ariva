namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>
/// A rehearsal (ARV-104i): the Ariva site and the running campaign the observers capture for, the scenario site and the seed
/// its day must run with (optional), the instant of the scenario's 00:00 on real time (<c>dayStartUtc</c>: for a planned day
/// in Dubai, the day before at 20:00Z), the clock minutes of the window, the injected errors and slips (none by default), the
/// minutes between tracers and the seed of the slips.
/// </summary>
public sealed record RehearsalRequest(
    string SiteCode,
    Guid? CampaignId,
    string ScenarioSite,
    long? ScenarioSeed,
    string DayStartUtc,
    int? FromMinute,
    int? ToMinute,
    double? CountErrorPercent,
    IReadOnlyList<string> CountErrorLines,
    double? TracerErrorMinutes,
    double? TracerErrorPercent,
    double? MissedBinsPercent,
    double? MissedMinutesPercent,
    int? TracerEveryMinutes,
    long? Seed);

/// <summary>A checked rehearsal request, as the emulator runs it.</summary>
internal sealed record Rehearsal(
    string SiteCode,
    Guid CampaignId,
    string ScenarioSite,
    uint? ScenarioSeed,
    DateTime DayStartUtc,
    RehearsalWindow Window,
    ObserverErrors Errors,
    int TracerEveryMinutes,
    uint? Seed);

/// <summary>The observer accounts to use from now on (replacing those configured or set before).</summary>
public sealed record ObserversRequest(IReadOnlyList<ObserverCredentials> Observers);

/// <summary>One observer as the status shows it: its number, whether a TOTP seed is configured, whether it holds a token now. Never its user name or a secret.</summary>
public sealed record ObserverStatus(int Observer, bool SecondFactor, bool SignedIn);

/// <summary>
/// The validation emulator: whether Ariva.Api.Main's address is configured, the observers, whether a rehearsal runs, the instant
/// the sensor emulator last laid the scenario's 00:00 on at speed 1 (the day start a rehearsal against the live pipeline uses),
/// and the last rehearsal's report.
/// </summary>
public sealed record ValidationEmulatorStatus(
    bool MainConfigured,
    IReadOnlyList<ObserverStatus> Observers,
    bool Running,
    DateTime? ClockDayStartUtc,
    RehearsalReport Last);

/// <summary>How one observer fared: its number and where its sign-in and campaign read left it.</summary>
public sealed record ObserverOutcome(int Observer, string State);

/// <summary>
/// What Ariva answered the calls of one kind: planned, recorded (201), the stored answer to a resend (200), refused as already
/// recorded or with a key used for another request (409), refused otherwise (400, 403, 404, 413), failed (no answer or a server
/// error after one more try, or the call that took its observer out of the rehearsal), and not sent because the observer slipped
/// or had been taken out of the rehearsal.
/// </summary>
public sealed record SubmissionTally(int Planned, int Recorded, int Replayed, int Conflicts, int Refused, int Failed, int NotSent);

/// <summary>One refusal Ariva gave: the kind of call, the observer, the HTTP status and Ariva's own title (Ariva never repeats a request value in it).</summary>
public sealed record RehearsalRefusal(string Kind, int Observer, int? Status, string Title);

/// <summary>
/// A rehearsal's report: what was played (campaign, Ariva site, scenario site and its seed, day start and window, errors), when
/// and by which operator key, the outcome, each observer's state, what Ariva answered per kind (counts, tracer batches, desk
/// batches), the tracer runs and desk minutes sent, the plan's summary and the first refusals. No user name, password, token or
/// code; observers are numbers.
/// </summary>
public sealed record RehearsalReport(
    Guid CampaignId,
    string SiteCode,
    string ScenarioSite,
    uint ScenarioSeed,
    DateTime DayStartUtc,
    int FromMinute,
    int ToMinute,
    ObserverErrors Errors,
    uint Seed,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string RunBy,
    string Outcome,
    IReadOnlyList<ObserverOutcome> Observers,
    SubmissionTally Counts,
    SubmissionTally TracerBatches,
    SubmissionTally DeskBatches,
    int TracerRuns,
    int DeskMinutes,
    PlanSummary Plan,
    IReadOnlyList<RehearsalRefusal> Refusals);

/// <summary>The outcomes of a rehearsal.</summary>
public static class RehearsalOutcomes
{
    /// <summary>The plan was worked through: each call's answer, failure or reason it was not sent is in the tallies (an observer taken out is <see cref="ObserverStates.TakenOut"/>).</summary>
    public const string Completed = "Completed";

    /// <summary>No observer could capture for the campaign (signed out, refused, without the permission, or the campaign is not running at the site).</summary>
    public const string NoObserverCanCapture = "NoObserverCanCapture";

    /// <summary>The rehearsal stopped at its time limit or because the caller left; the tallies say how far it got.</summary>
    public const string Stopped = "Stopped";
}

/// <summary>Where an observer's sign-in and campaign read left it, beside <see cref="ObserverSignIn"/>.</summary>
public static class ObserverStates
{
    /// <summary>Signed in and shown the campaign: it captures.</summary>
    public const string Capturing = "Capturing";

    /// <summary>Ariva answered 403 to the capture list: the account does not hold Validation.Capture.</summary>
    public const string CannotCapture = "CannotCapture";

    /// <summary>Ariva answered 404: the account does not have the site.</summary>
    public const string SiteNotVisible = "SiteNotVisible";

    /// <summary>The campaign is not among the running campaigns the account is shown (not running, or of another site).</summary>
    public const string CampaignNotRunning = "CampaignNotRunning";

    /// <summary>
    /// Taken out of the rehearsal: its sign-in while sending failed, it would have needed a third sign-in, or Ariva answered 401
    /// to a token it had just issued (a disabled, reset or locked account); its remaining items were not sent (the refusals say why).
    /// </summary>
    public const string TakenOut = "TakenOut";
}
