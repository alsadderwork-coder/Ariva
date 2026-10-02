namespace Ariva.Core.Domain.Enums;

/// <summary>The kind of people-sensing device (wiki/09 adapter matrix, ARV-021); stored by name.</summary>
public enum DeviceFamily
{
    /// <summary>Overhead 3D stereo vision (Xovis PC2, PC3); rectangular coverage footprint.</summary>
    StereoVision,

    /// <summary>LiDAR through a perception platform (never raw point clouds); radius coverage.</summary>
    Lidar,

    /// <summary>Camera 3D or AI people counter, or an analytics server in front of existing CCTV.</summary>
    CameraAnalytics,

    /// <summary>Thermal or time-of-flight overhead counter.</summary>
    ThermalOrTimeOfFlight,

    /// <summary>Ariva.Simulation.Api replaying or generating tracks (dev and demo).</summary>
    Simulator
}

/// <summary>How a device's output reaches Ariva (wiki/09 transports); stored by name.</summary>
public enum DeviceTransport
{
    HttpsPush,
    Mqtt,
    RestPull,
    WebSocket,
    TcpOrUdp,
    FileDrop,
    OnvifProfileM
}

/// <summary>The payload format the ingest maps to canonical events (ARV-023, ARV-024); stored by name.</summary>
public enum DeviceDialect
{
    /// <summary>Ariva's canonical events as they are.</summary>
    Canonical,

    /// <summary>The coded Xovis mapper.</summary>
    Xovis,

    /// <summary>A declarative path mapper stored with the device family.</summary>
    Declarative
}

/// <summary>
/// The lifecycle of a device (wiki/07): <c>Commissioning</c> until a calibration passes, then <c>Online</c>, with
/// <c>Degraded</c> and <c>Offline</c> set by health; <c>Retired</c> is final. Stored by name.
/// </summary>
public enum DeviceState
{
    Commissioning,
    Online,
    Degraded,
    Offline,
    Retired
}

/// <summary>The device's time source; offsets are measured against the site reference (F19).</summary>
public enum ClockSource
{
    Ntp,
    Ptp
}

/// <summary>Where a coverage footprint comes from.</summary>
public enum FootprintSource
{
    /// <summary>The vendor's footprint table for the model and height, entered by the installer (authoritative).</summary>
    Vendor,

    /// <summary>The BOQ's planning assumption for the mounting height (labelled as an estimate everywhere it shows).</summary>
    AssumedFromBoq
}

/// <summary>How a calibration's accuracy was measured (wiki/07 section 6).</summary>
public enum CalibrationMethod
{
    /// <summary>One observer with tally counters against the device's counts.</summary>
    ManualCountTally,

    /// <summary>Two observers whose counts are reconciled before comparison.</summary>
    ManualCountTwoObservers
}

/// <summary>Which way a line was crossed, relative to the line's direction (canonical <c>LineCrossing</c>).</summary>
public enum CrossingDirection
{
    In,
    Out
}
