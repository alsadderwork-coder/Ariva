namespace Ariva.Infra.Settings;

/// <summary>
/// The host's stop budget (ARV-165), section <c>Hosting</c>. The host's shutdown timeout is
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
}
