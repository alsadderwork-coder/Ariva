using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// The level at which a desk's observed minute is judged (F18 desk-state agreement, ARV-149a; owner decision 2026-10-10,
/// docs/product/decisions.md): from Ariva's own record of the minute, the zone profile in force then, never the observers.
/// </summary>
public enum DeskAgreementLevel
{
    /// <summary>
    /// On the full state (Closed, Idle, Serving, Paused): a desk with a staff or service zone in the zone profile in force, a
    /// minute Ariva's record says nothing about (fail closed), and every minute of results frozen before ARV-149a.
    /// </summary>
    FullState = 0,

    /// <summary>
    /// At the throughput level, open (Idle or Serving, F8's n_open) against not open (Paused or Closed): a desk without a staff or
    /// service zone, whose state comes from AMAN alone (F10 rows 5a to 5f) and cannot tell Serving from Idle desk by desk.
    /// </summary>
    Throughput = 1,

    /// <summary>A desk's or the campaign's summary only, never a minute's: some judged minutes at each level (a desk's zones changed during the campaign).</summary>
    Mixed = 2
}

/// <summary>
/// One observed desk minute against the desk's stored minute (ARV-104f, F18 desk-state agreement): how many observers recorded
/// it (each by their latest revision), the state they saw (null when they disagree: the minute has no ground truth and is
/// not judged), the dominant state of the stored minute (<see cref="DeskStateAgreement.Dominant"/>; Unknown when no usable
/// row was stored, <see cref="SystemStored"/> false), its standing (Good; Degraded when the stored minute is flagged or part
/// of it is Unknown or unaccounted; Unknown when the dominant state is Unknown or no usable row exists), the level it is judged
/// at (ARV-149a: <see cref="DeskAgreementLevel.FullState"/> or <see cref="DeskAgreementLevel.Throughput"/>) and whether the two
/// agree at that level (Unknown never agrees; null when not judged). Border per-desk data: no person is named (not even the observers, only
/// their count), and ARV-104g serves it to callers who see border desks of the campaign's site only (data boundary).
/// </summary>
public sealed record DeskMinuteAgreement(
    int ProfileVersion,
    Guid DeskId,
    string CheckpointCode,
    string DeskCode,
    DateTime MinuteUtc,
    int Observers,
    ObservedDeskState? ObservedState,
    bool ObserversDisagree,
    DeskStatus SystemState,
    bool SystemStored,
    ComparisonStanding Standing,
    bool? Agrees,
    DeskAgreementLevel Level = DeskAgreementLevel.FullState);

/// <summary>How many judged minutes an observer saw in one state while the stored minute's dominant state was another (or the same).</summary>
public sealed record DeskStateCount(ObservedDeskState Observed, DeskStatus System, int Minutes);

/// <summary>
/// Desk-state agreement of one border desk, or of every desk in scope (<see cref="DeskId"/> and the codes null), F18: the
/// observed minutes listed, those judged (every observer of the minute agreeing on its state), those agreeing with the stored
/// dominant state, the minutes excluded (observers disagreeing, or an observer's latest revision unusable,
/// <see cref="UnusableKey"/>), the judged minutes without a usable stored minute (counted as Unknown, so as disagreement), the
/// judged minutes by standing (Good, Degraded, Unknown: all of them judged), the agreement on whether the desk counted for
/// throughput (Idle or Serving, F8's n_open; no target), the observed state against the dominant state for every pair (four
/// observed states by five stored ones, in enum order) and the verdict: agreeing over judged minutes at or above the target
/// (F18: 95 percent), no data with none judged. Beside it, <see cref="StrictAgreement"/>: agreeing minutes over every observed
/// minute, the excluded ones counted as disagreements (fail closed).
/// <para>
/// ARV-149a: each judged minute agrees at its own level (<see cref="DeskAgreementLevel"/>), so <see cref="Agreeing"/>, the check
/// and <see cref="StrictAgreement"/> pool both levels; <see cref="JudgedAtThroughput"/> says how many judged minutes were judged
/// at the throughput level (the rest on the full state), <see cref="Level"/> names it, and, with no target,
/// <see cref="ServingIdleAgreement"/> says how often those minutes agree on Serving against Idle when the observer and the stored
/// minute both say the desk was open (<see cref="ServingIdleMinutes"/> of them, <see cref="ServingIdleAgreeing"/> agreeing).
/// Results frozen before ARV-149a hold none of these: read back, they are 0 (every minute judged on the full state, as it was),
/// so their documents and content hashes stay as stored.
/// </para> The verdict judges only the minutes it holds, so it is not
/// an acceptance verdict on its own: the campaign's verdict (ARV-104g) needs the campaign's own target of judged minutes and
/// shows the excluded share.
/// <para>
/// The exclusion lever (security review of ARV-104f, Medium): a minute whose observers disagree is excluded, so a second
/// observer can take minutes out of the agreement. One observer logs Serving for 20 minutes and the stored states match 16
/// (0.80, a fail); a second observer who logs Closed on just the 4 wrong minutes makes them disagree among observers, and the
/// agreement becomes 16 of 16 (1.00, a pass), while <see cref="StrictAgreement"/> stays 0.80. The campaign's verdict (ARV-104g)
/// must therefore count <see cref="ObserversDisagree"/> and <see cref="UnusableObservations"/> against the system (the strict
/// agreement) or cap their share of the observed minutes.
/// </para>
/// Border per-desk data, also over every desk (the campaign's border desks): ARV-104g serves it to callers who see border desks
/// of the campaign's site only, never to airport roles, the border-to-airport feed, AMAN or an airport deployment.
/// </summary>
public sealed record DeskAgreementSummary(
    int ProfileVersion,
    Guid? DeskId,
    string CheckpointCode,
    string DeskCode,
    int Minutes,
    int Judged,
    int Agreeing,
    int ObserversDisagree,
    int UnusableObservations,
    int Excluded,
    int WithoutSystemMinute,
    StandingTally Standings,
    double? ThroughputAgreement,
    double? StrictAgreement,
    IReadOnlyList<DeskStateCount> Confusion,
    CriterionCheck Check,
    int JudgedAtThroughput = 0,
    int ServingIdleMinutes = 0,
    int ServingIdleAgreeing = 0)
{
    /// <summary>
    /// The level the judged minutes were judged at (ARV-149a): the full state when none was at the throughput level, the throughput
    /// level when all were, mixed otherwise; null with none judged. Computed from the counts, so it is never stored apart from them.
    /// </summary>
    public DeskAgreementLevel? Level => Judged <= 0 ? null
        : JudgedAtThroughput <= 0 ? DeskAgreementLevel.FullState
        : JudgedAtThroughput >= Judged ? DeskAgreementLevel.Throughput
        : DeskAgreementLevel.Mixed;

    /// <summary>Serving against Idle at the throughput level (ARV-149a, no target): agreeing over compared minutes; null with none.</summary>
    public double? ServingIdleAgreement => DeskStateAgreement.Agreement(ServingIdleAgreeing, ServingIdleMinutes);
}
