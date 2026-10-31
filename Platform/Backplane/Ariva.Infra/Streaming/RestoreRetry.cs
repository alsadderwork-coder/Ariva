namespace Ariva.Infra.Streaming;

/// <summary>
/// When the restores of an assignment (ARV-274) try again after the database did not answer, and when they give up: each failure
/// waits <c>after</c> on the host's clock, and the failure that makes <c>attempts</c> in a row gives up and starts afresh (CWE-400: a
/// database that does not answer costs the loop one failed read an attempt, never one a zone: the listing's 10 s, or a state read's
/// Database:CommandTimeoutSeconds or the connect timeout).
/// </summary>
internal sealed class RestoreRetry(TimeProvider timeProvider, TimeSpan after, int attempts)
{
    #region Fields

    private readonly TimeProvider _clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TimeSpan _after = after >= TimeSpan.Zero ? after : throw new ArgumentOutOfRangeException(nameof(after));
    private readonly int _attempts = attempts >= 1 ? attempts : throw new ArgumentOutOfRangeException(nameof(attempts));
    private DateTime _due = DateTime.MinValue;

    #endregion

    #region Properties

    /// <summary>The failures in a row so far.</summary>
    public int Failures { get; private set; }

    /// <summary>Whether the restores may run now: not before the wait after a failure has passed.</summary>
    public bool Due => _clock.GetUtcNow().UtcDateTime >= _due;

    #endregion

    #region Methods

    /// <summary>One more failure: false while the restores try again (not before the wait), true when they give up (and start afresh).</summary>
    public bool Failed()
    {
        if (++Failures < _attempts)
        {
            _due = _clock.GetUtcNow().UtcDateTime + _after;
            return false;
        }

        Reset();
        return true;
    }

    /// <summary>A pass the database answered: the failures in a row start again from none.</summary>
    public void Succeeded() => Failures = 0;

    /// <summary>No failure and no wait: an assignment's restores ended (done, given up, or their partitions taken away).</summary>
    public void Reset()
    {
        Failures = 0;
        _due = DateTime.MinValue;
    }

    #endregion
}
