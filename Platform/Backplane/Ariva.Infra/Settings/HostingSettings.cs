namespace Ariva.Infra.Settings;

/// <summary>
/// The host's stop budget (ARV-165) and start budget (ARV-176), section <c>Hosting</c>. The host's shutdown timeout is
/// <see cref="ShutdownSeconds"/>, of which the message bus gets all but 15 seconds (the endpoints the bus skipped, the
/// producer's flush and the rest); the pod's grace period, <see cref="GracePeriodSeconds"/>, must leave
/// <see cref="ExitMarginSeconds"/> after it for the dispose and the process's exit. The chart sets the grace period on every
/// API pod and passes the same value here (<c>Hosting__GracePeriodSeconds</c>), so the host can check the two agree.
/// </summary>
public sealed class HostingSettings
{
    public const string SectionName = "Hosting";

    /// <summary>The seconds between the end of the host's stop and the pod's grace period, for the dispose and the exit.</summary>
    public const int ExitMarginSeconds = 5;

    /// <summary>The host's shutdown timeout (.NET's <c>HostOptions.ShutdownTimeout</c>), 50 seconds.</summary>
    public int ShutdownSeconds { get; init; } = 50;

    /// <summary>The pod's <c>terminationGracePeriodSeconds</c> (60 in the chart): after it the kubelet kills the process.</summary>
    public int GracePeriodSeconds { get; init; } = 60;

    /// <summary>
    /// The seconds of the pod's startup allowance kept for everything in the host's start but the consumers' waits for
    /// their topics and for their readiness (ARV-176): the bus's start (at most 15 s, its <c>StartTimeout</c>; when it
    /// times out the start fails there, before the waits), a metadata request that overruns the topic wait (at most 12 s:
    /// a 2 s pause and a 10 s request) and the process up to the bus (3 s left at the largest topic wait the default
    /// allowance accepts, 70 s; 13 s at the default, 60 s).
    /// </summary>
    public const int StartMarginSeconds = 30;

    /// <summary>
    /// The pod's startup allowance (ARV-176): the least time its startup probe gives the host before the kubelet restarts
    /// it, <c>initialDelaySeconds</c> plus <c>periodSeconds</c> times (<c>failureThreshold</c> less 1), which is
    /// 10 + 15 x (9 - 1) = 130 in the chart. The kubelet's prober ticks every period from before the container starts and
    /// skips the ticks within the initial delay, so the first counted tick can come at the initial delay itself and the
    /// restart at the threshold's failure that many periods later (a refused connection fails at once; the probe's timeout
    /// adds nothing). The host's start holds its web server, so <c>Kafka:Consumers:StartWaitSeconds</c>, the 30 second
    /// ready wait and <see cref="StartMarginSeconds"/> must fit in it. The chart passes the value
    /// (<c>Hosting__StartupAllowanceSeconds</c>) and its render test checks it against the probe.
    /// </summary>
    public int StartupAllowanceSeconds { get; init; } = 130;
}
