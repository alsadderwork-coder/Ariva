using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios;
using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>Why a rehearsal could not start, beside its report.</summary>
public enum RehearsalRefused
{
    None,

    /// <summary>Another rehearsal is running (one at a time).</summary>
    Busy,

    /// <summary>No Ariva.Api.Main address is configured (<c>Simulation:Ariva:MainUrl</c>).</summary>
    NoArivaAddress,

    /// <summary>No observer account is configured.</summary>
    NoObservers,

    /// <summary>The scenario site's day runs with another seed than the one the request names.</summary>
    OtherSeed,

    /// <summary>The operator key started its rehearsals of this minute already (<see cref="ValidationEmulatorSettings.RehearsalsPerMinute"/>).</summary>
    RateLimited
}

/// <summary>
/// The validation observer emulator (ARV-104i): the scenario knows the reference evening's true counts, waits and desk states,
/// and this emulator reports them to Ariva as validation observers would, through the capture API of Ariva.Api.Main, signed
/// in as Ariva accounts with the Validation observer role (<see cref="ArivaObserverClient"/>), with optional injected error
/// (<see cref="ObserverErrors"/>). It rehearses the whole validation path before any site work: campaign, capture, results.
/// One rehearsal at a time; it reads the scenario day under the engine's lock, plans (<see cref="RehearsalPlanner"/>) and sends
/// in order: counts, tracer batches, desk batches. Every Idempotency-Key is derived from the campaign, the observer's account
/// and the item, so a rehearsal sent twice records nothing twice. Ariva decides everything: a campaign's creator or starter,
/// an account without the permission, a site the account does not have, a campaign not running, a bin outside the planned
/// days are Ariva's refusals, reported as such and never worked around.
/// </summary>
public sealed class ValidationEmulator : IDisposable
{
    public const string HttpClientName = "ariva-main";

    private const int MaxRefusals = 20;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly ScenarioEngine _engine;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<ArivaTargetSettings> _target;
    private readonly IOptionsMonitor<ValidationEmulatorSettings> _settings;
    private readonly SensorEmulator _clock;
    private readonly TimeProvider _time;
    private readonly ILogger<ValidationEmulator> _logger;
    private readonly PartitionedRateLimiter<string> _perKey;

    // The last TOTP step each observer account used, by user name as Ariva matches it, kept across replacements of the observers (never a secret).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _steps = new(StringComparer.Ordinal);
    private ArivaObserverClient[] _observers;
    private RehearsalReport _last;

    public ValidationEmulator(ScenarioEngine engine, IHttpClientFactory http, IOptionsMonitor<ArivaTargetSettings> target,
        IOptionsMonitor<ValidationEmulatorSettings> settings, SensorEmulator clock, TimeProvider time, ILogger<ValidationEmulator> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _engine = engine;
        _http = http;
        _target = target;
        _settings = settings;
        _clock = clock;
        _time = time;
        _logger = logger;
        _observers = Clients(settings.CurrentValue.Observers);
        // Counted after the request passed its checks, so a refused request costs nothing and is answered 400, not 429.
        _perKey = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.CurrentValue.RehearsalsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }), StringComparer.Ordinal);
    }

    private ArivaObserverClient[] Clients(IReadOnlyList<ObserverCredentials> observers) =>
        [.. (observers ?? []).Select((credentials, i) => new ArivaObserverClient(i + 1, credentials, () => _http.CreateClient(HttpClientName), _time, _logger, _steps))];

    #region Status and observers

    public ValidationEmulatorStatus Status()
    {
        lock (_gate)
        {
            return new ValidationEmulatorStatus(!string.IsNullOrEmpty(_target.CurrentValue.MainUrl),
                [.. _observers.Select(o => new ObserverStatus(o.Observer, o.HasSecondFactor, o.SignedIn))], _running.CurrentCount == 0,
                _clock?.PlayedDayStartUtc, _last);
        }
    }

    /// <summary>
    /// Replaces the observer accounts (checked by the caller), unless a rehearsal is running (false then); the sessions of the
    /// accounts replaced are signed out. Logged with the operator key's name and the count only.
    /// </summary>
    public async Task<bool> UseObserversAsync(IReadOnlyList<ObserverCredentials> observers, string by, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct))
            return false;
        try
        {
            ArivaObserverClient[] previous;
            lock (_gate)
            {
                previous = _observers;
                _observers = Clients(observers);
            }

            foreach (var client in previous)
                await client.SignOutAsync(ct);
            _logger.LogInformation("Validation observers replaced by {Operator}: {Count} accounts", by, observers?.Count ?? 0);
            return true;
        }
        finally
        {
            _running.Release();
        }
    }

    #endregion

    #region Rehearsal

    /// <summary>Runs a rehearsal (checked by the caller) and returns its report, or why it could not start.</summary>
    internal async Task<(RehearsalRefused Refused, RehearsalReport Report)> RehearseAsync(Rehearsal rehearsal, string by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rehearsal);
        if (string.IsNullOrEmpty(_target.CurrentValue.MainUrl))
            return (RehearsalRefused.NoArivaAddress, null);
        using (var permit = _perKey.AttemptAcquire(by ?? string.Empty))
        {
            if (!permit.IsAcquired)
                return (RehearsalRefused.RateLimited, null);
        }

        if (!await _running.WaitAsync(0, ct))
            return (RehearsalRefused.Busy, null);
        try
        {
            ArivaObserverClient[] observers;
            lock (_gate)
                observers = _observers;
            if (observers.Length == 0)
                return (RehearsalRefused.NoObservers, null);
            var runningSeed = _engine.Read(rehearsal.ScenarioSite, d => d.Seed);
            if (rehearsal.ScenarioSeed is { } expected && expected != runningSeed)
                return (RehearsalRefused.OtherSeed, null);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(_settings.CurrentValue.RehearsalTimeoutSeconds));
            var report = await RunAsync(rehearsal, observers, by, limit.Token, ct);
            lock (_gate)
                _last = report;
            _logger.LogInformation(
                "Validation rehearsal by {Operator}: campaign {CampaignId} at {SiteCode}, scenario {ScenarioSite} seed {ScenarioSeed}, minutes {From} to {To}: {Outcome}; " +
                "counts {CountsRecorded} recorded, {CountsReplayed} replayed, {CountsRefused} refused; tracer batches {TracersRecorded} recorded; desk batches {DesksRecorded} recorded",
                by, rehearsal.CampaignId, rehearsal.SiteCode, rehearsal.ScenarioSite, report.ScenarioSeed, rehearsal.Window.FromMinute, rehearsal.Window.ToMinute,
                report.Outcome, report.Counts.Recorded, report.Counts.Replayed, report.Counts.Refused, report.TracerBatches.Recorded, report.DeskBatches.Recorded);
            return (RehearsalRefused.None, report);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<RehearsalReport> RunAsync(Rehearsal rehearsal, ArivaObserverClient[] observers, string by, CancellationToken ct, CancellationToken caller)
    {
        var started = _time.GetUtcNow();
        var site = Uri.EscapeDataString(rehearsal.SiteCode);
        var campaignPath = $"api/v1/sites/{site}/validation/capture/campaigns";
        var outcomes = new List<ObserverOutcome>();
        var capturing = new List<(ArivaObserverClient Client, CaptureCampaign Campaign)>();
        var refusals = new List<RehearsalRefusal>();
        var stopped = false;

        // Each observer signs in and reads the running campaigns of the site as Ariva shows them to it.
        foreach (var client in observers)
        {
            client.BeginRehearsal();
            try
            {
                var signIn = await client.SignInAsync(ct);
                if (signIn != ObserverSignIn.SignedIn)
                {
                    outcomes.Add(new ObserverOutcome(client.Observer, signIn.ToString()));
                    continue;
                }

                var (answer, running) = await client.GetAsync<List<CaptureCampaign>>(campaignPath, ct);
                var campaign = running?.FirstOrDefault(c => c?.Id == rehearsal.CampaignId);
                var state = answer.Status switch
                {
                    _ when answer.ObserverOut => ObserverStates.TakenOut,
                    403 => ObserverStates.CannotCapture,
                    404 => ObserverStates.SiteNotVisible,
                    _ when !answer.Succeeded => ObserverSignIn.Unreachable.ToString(),
                    _ when campaign is null => ObserverStates.CampaignNotRunning,
                    _ => ObserverStates.Capturing
                };
                outcomes.Add(new ObserverOutcome(client.Observer, state));
                if (campaign is not null && state == ObserverStates.Capturing)
                    capturing.Add((client, campaign));
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                stopped = true;
                break;
            }
        }

        var seed = rehearsal.Seed ?? _engine.Read(rehearsal.ScenarioSite, d => d.Seed);
        if (capturing.Count == 0)
        {
            var none = new SubmissionTally(0, 0, 0, 0, 0, 0, 0);
            return Report(rehearsal, by, started, stopped ? RehearsalOutcomes.Stopped : RehearsalOutcomes.NoObserverCanCapture, outcomes, none, none, none, 0, 0,
                new PlanSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), refusals, seed);
        }

        // Ariva's scope as the first capturing observer sees it; desks go to the observers Ariva shows them to.
        var scope = capturing[0].Campaign;
        var deskObservers = capturing.Select((c, i) => (c.Campaign, Index: i)).Where(c => c.Campaign.DesksIncluded && (c.Campaign.Desks?.Count ?? 0) > 0)
            .Select(c => c.Index).ToList();
        var (plan, scenarioSeed) = _engine.Read(rehearsal.ScenarioSite, day =>
            (RehearsalPlanner.Plan(new ValidationTruth(day, rehearsal.DayStartUtc), scope, capturing.Count, deskObservers, rehearsal.Window, rehearsal.Errors, seed,
                rehearsal.TracerEveryMinutes), day.Seed));
        var calls = plan.Counts.Count + plan.DeskBatches.Count + plan.Runs.Count;
        if (calls > ValidationEmulatorSettings.MaxCalls)
        {
            refusals.Add(new RehearsalRefusal("Plan", 0, null, "The rehearsal plans more calls than one rehearsal sends; narrow the window or the campaign's scope."));
            var none = new SubmissionTally(0, 0, 0, 0, 0, 0, 0);
            return Report(rehearsal, by, started, RehearsalOutcomes.Stopped, outcomes, none, none, none, 0, 0, plan.Summary, refusals, seed, scenarioSeed);
        }

        var counts = new Tally(plan.Counts.Count + plan.Summary.CountBinsMissed, plan.Summary.CountBinsMissed);
        var tracers = new Tally(0, 0);
        var desks = new Tally(plan.DeskBatches.Count + plan.Summary.DeskBatchesMissed, plan.Summary.DeskBatchesMissed);
        var runsSent = 0;
        var deskMinutes = 0;
        var basePath = $"{campaignPath}/{rehearsal.CampaignId:D}";
        try
        {
            foreach (var count in plan.Counts)
            {
                var (client, _) = capturing[count.Observer];
                var answer = await client.SendAsync(HttpMethod.Post, $"{basePath}/counts", () => new
                {
                    lineId = count.LineId,
                    binStartUtc = RehearsalPlanner.Stamp(count.BinStartUtc),
                    crossingsIn = count.CrossingsIn,
                    crossingsOut = count.CrossingsOut
                }, Key(rehearsal.CampaignId, client.Account, "count", $"{count.LineId:N}|{RehearsalPlanner.Stamp(count.BinStartUtc)}"), ct);
                counts.Add(answer, "Count", client.Observer, refusals);
            }

            // Tracer runs: each observer's runs in batches of up to 20, each batch with the device's (the simulator's) clock read at every send.
            foreach (var observer in plan.Runs.GroupBy(r => r.Observer).OrderBy(g => g.Key))
            {
                var (client, _) = capturing[observer.Key];
                foreach (var batch in observer.Chunk(20))
                {
                    tracers.Planned++;
                    var answer = await client.SendAsync(HttpMethod.Post, $"{basePath}/tracer-runs", () => new
                    {
                        deviceClockUtc = RehearsalPlanner.Stamp(_time.GetUtcNow().UtcDateTime),
                        runs = batch.Select(r => new
                        {
                            zoneId = r.ZoneId,
                            tracerCode = r.TracerCode,
                            joinedUtc = RehearsalPlanner.Stamp(r.JoinedUtc),
                            exitedUtc = RehearsalPlanner.Stamp(r.ExitedUtc),
                            abandoned = false
                        }).ToList()
                    }, Key(rehearsal.CampaignId, client.Account, "tracers", string.Join(',', batch.Select(r => r.TracerCode + "@" + RehearsalPlanner.Stamp(r.JoinedUtc)))), ct);
                    tracers.Add(answer, "TracerBatch", client.Observer, refusals);
                    if (answer.Succeeded)
                        runsSent += batch.Length;
                }
            }

            foreach (var batch in plan.DeskBatches)
            {
                var (client, _) = capturing[batch.Observer];
                var answer = await client.SendAsync(HttpMethod.Post, $"{basePath}/desk-observations", () => new
                {
                    binStartUtc = RehearsalPlanner.Stamp(batch.BinStartUtc),
                    desks = batch.Desks.Select(d => new { deskId = d.DeskId, states = d.States }).ToList()
                }, Key(rehearsal.CampaignId, client.Account, "desks", $"{RehearsalPlanner.Stamp(batch.BinStartUtc)}|{string.Join(',', batch.Desks.Select(d => d.DeskId.ToString("N")))}"), ct);
                desks.Add(answer, "DeskBatch", client.Observer, refusals);
                if (answer.Succeeded)
                    deskMinutes += batch.Desks.Sum(d => d.States.Count(s => s is not null));
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            stopped = true;
        }

        // An observer taken out while sending (its sign-in failed, or Ariva refused a token it had just issued) says so in the report.
        for (var i = 0; i < outcomes.Count; i++)
        {
            if (capturing.Any(c => c.Client.Observer == outcomes[i].Observer && c.Client.TakenOut is not null))
                outcomes[i] = outcomes[i] with { State = ObserverStates.TakenOut };
        }

        return Report(rehearsal, by, started, stopped ? RehearsalOutcomes.Stopped : RehearsalOutcomes.Completed, outcomes, counts.ToTally(), tracers.ToTally(),
            desks.ToTally(), runsSent, deskMinutes, plan.Summary, refusals, seed, scenarioSeed);
    }

    private RehearsalReport Report(Rehearsal rehearsal, string by, DateTimeOffset started, string outcome, List<ObserverOutcome> observers, SubmissionTally counts,
        SubmissionTally tracers, SubmissionTally desks, int runs, int deskMinutes, PlanSummary plan, List<RehearsalRefusal> refusals, uint seed, uint? scenarioSeed = null) =>
        new(rehearsal.CampaignId, rehearsal.SiteCode, rehearsal.ScenarioSite, scenarioSeed ?? _engine.Read(rehearsal.ScenarioSite, d => d.Seed), rehearsal.DayStartUtc,
            rehearsal.Window.FromMinute, rehearsal.Window.ToMinute, rehearsal.Errors, seed, started, _time.GetUtcNow(), by, outcome, observers, counts, tracers, desks, runs,
            deskMinutes, plan, refusals);

    /// <summary>
    /// The Idempotency-Key of one item: "sim-" and 40 hexadecimal characters of SHA-256 over the campaign, the observer's account,
    /// the kind and the item, so a resend of the same item by the same account is the same request, and nothing else is.
    /// </summary>
    internal static string Key(Guid campaignId, string userName, string kind, string item)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{campaignId:N}\n{userName}\n{kind}\n{item}")));
        return "sim-" + Convert.ToHexStringLower(hash)[..40];
    }

    public void Dispose()
    {
        _running.Dispose();
        _perKey.Dispose();
    }

    /// <summary>Counts what Ariva answered one kind of call.</summary>
    private sealed class Tally(int planned, int notSent)
    {
        private int _recorded, _replayed, _conflicts, _refused, _failed;
        private int _notSent = notSent;

        public int Planned { get; set; } = planned;

        public void Add(ArivaAnswer answer, string kind, int observer, List<RehearsalRefusal> refusals)
        {
            // Not sent: the observer was taken out of the rehearsal before this item (the item that took it out failed, below).
            if (answer.NotSent)
            {
                _notSent++;
                return;
            }

            if (answer.ObserverOut)
            {
                _failed++;
                if (refusals.Count < MaxRefusals)
                    refusals.Add(new RehearsalRefusal(kind, observer, answer.Status, answer.Title));
                return;
            }

            switch (answer.Status)
            {
                case 201:
                    _recorded++;
                    return;
                case 200:
                    _replayed++;
                    return;
                case 409:
                    _conflicts++;
                    break;
                case null or >= 500 or 408 or 429:
                    _failed++;
                    break;
                default:
                    _refused++;
                    break;
            }

            if (refusals.Count < MaxRefusals)
                refusals.Add(new RehearsalRefusal(kind, observer, answer.Status, answer.Title));
        }

        public SubmissionTally ToTally() => new(Planned, _recorded, _replayed, _conflicts, _refused, _failed, _notSent);
    }

    #endregion
}
