using System.Collections.Concurrent;

namespace Ariva.Infra.Alerting;

/// <summary>
/// Which stored rules the live evaluation has already warned about as not evaluated, per host process (ARV-187, lead
/// decision P3, superseding ARV-181b2's warning per tick): a border rule that fails its metric's rule checks is warned
/// about once per rule and values version (<see cref="Ariva.Core.Domain.Entities.AlertRule.ValuesHash"/>), again when its
/// values change, and again after it was evaluated in between (<see cref="Forget"/>). A singleton: a restarted host warns
/// once more. The ticks run one at a time (the evaluation's advisory lock), and the map holds one short string per such
/// rule of the database at most.
/// </summary>
public sealed class NotEvaluatedWarnings
{
    #region Fields

    private readonly ConcurrentDictionary<Guid, string> _warned = new();

    #endregion

    #region Methods

    /// <summary>
    /// Whether to warn about <paramref name="ruleId"/> now: true the first time, and when <paramref name="valuesHash"/>
    /// differs from the version last warned about; false for the same version again. Records the version.
    /// </summary>
    public bool ShouldWarn(Guid ruleId, string valuesHash)
    {
        var version = valuesHash ?? string.Empty;
        var warn = true;
        _warned.AddOrUpdate(ruleId, version, (_, seen) =>
        {
            warn = !string.Equals(seen, version, StringComparison.Ordinal);
            return version;
        });
        return warn;
    }

    /// <summary>The rule is evaluated (or no longer enabled): a later failure of the checks warns again.</summary>
    public void Forget(Guid ruleId) => _warned.TryRemove(ruleId, out _);

    #endregion
}
