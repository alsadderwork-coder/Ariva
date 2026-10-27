using System.Collections.Concurrent;

namespace Ariva.Infra.Alerting;

/// <summary>
/// Which stored rules the live evaluation has already warned about as not evaluated, per host process (ARV-187, lead
/// decision P3, superseding ARV-181b2's warning per tick): a border rule that fails its metric's rule checks is warned
/// about once per rule and values version (<see cref="Ariva.Core.Domain.Entities.AlertRule.ValuesHash"/>), again when its
/// values change, and again after it was evaluated in between (<see cref="Forget"/>). A singleton: a restarted host warns
/// once more. The ticks run one at a time (the evaluation's advisory lock). Since ARV-190 a version is recorded only once
/// the warning's unit of work has committed (<see cref="Warned"/>), so a failed commit warns again at the next tick, and
/// each tick keeps only the rules it found enabled (<see cref="Keep"/>), so the map holds one short string per enabled rule
/// of the database at most, not the rules deleted since.
/// </summary>
public sealed class NotEvaluatedWarnings
{
    #region Fields

    private readonly ConcurrentDictionary<Guid, string> _warned = new();

    #endregion

    #region Methods

    /// <summary>
    /// Whether to warn about <paramref name="ruleId"/> now: true the first time, and when <paramref name="valuesHash"/>
    /// differs from the version last warned about; false for the same version again. Records nothing: the caller records the
    /// version with <see cref="Warned"/> once the warning's unit of work has committed (ARV-190).
    /// </summary>
    public bool ShouldWarn(Guid ruleId, string valuesHash) =>
        !_warned.TryGetValue(ruleId, out var seen) || !string.Equals(seen, valuesHash ?? string.Empty, StringComparison.Ordinal);

    /// <summary>Records that <paramref name="ruleId"/> was warned about at <paramref name="valuesHash"/>: after the commit (ARV-190).</summary>
    public void Warned(Guid ruleId, string valuesHash) => _warned[ruleId] = valuesHash ?? string.Empty;

    /// <summary>The rule is evaluated (or no longer enabled): a later failure of the checks warns again.</summary>
    public void Forget(Guid ruleId) => _warned.TryRemove(ruleId, out _);

    /// <summary>
    /// Forgets every rule but <paramref name="ruleIds"/>, the enabled rules a tick found (ARV-190): a rule deleted or disabled
    /// since its warning is no longer kept, so the map stays within the rules of the database.
    /// </summary>
    public void Keep(IReadOnlyCollection<Guid> ruleIds)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);
        var kept = new HashSet<Guid>(ruleIds);
        foreach (var ruleId in _warned.Keys)
            if (!kept.Contains(ruleId))
                _warned.TryRemove(ruleId, out _);
    }

    #endregion
}
