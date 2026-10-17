using System.Collections.Concurrent;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// How many advertisement uploads each person may attempt (ARV-142a, CWE-400, security review): at most
/// <see cref="MaxAttempts"/> in a fixed window of <see cref="Window"/>, counted per account before the file is read or
/// decoded, refused attempts included, so one account cannot keep the decoder busy back to back. The rate limiter of the
/// endpoint runs before authentication and cannot tell people apart; this counts after it, per host process (each replica
/// of Ariva.Api.Main counts its own). The upload policy's one-at-a-time limit still bounds the host as a whole.
/// </summary>
internal sealed class AdvertisingUploadWindow(TimeProvider timeProvider)
{
    public const int MaxAttempts = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Above this many accounts, windows that have ended are dropped on the next attempt.</summary>
    private const int PruneAbove = 1000;

    private readonly ConcurrentDictionary<Guid, (DateTime Start, int Count)> _attempts = new();

    /// <summary>Counts an attempt of the account; false when its window is used up (the attempt is then not counted).</summary>
    public bool TryEnter(Guid account)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (_attempts.Count > PruneAbove)
        {
            foreach (var (key, value) in _attempts)
            {
                if (now - value.Start >= Window)
                    _attempts.TryRemove(key, out _);
            }
        }

        var entered = false;
        _attempts.AddOrUpdate(account,
            _ =>
            {
                entered = true;
                return (now, 1);
            },
            (_, current) =>
            {
                if (now - current.Start >= Window)
                {
                    entered = true;
                    return (now, 1);
                }

                entered = current.Count < MaxAttempts;
                return entered ? (current.Start, current.Count + 1) : current;
            });
        return entered;
    }
}
