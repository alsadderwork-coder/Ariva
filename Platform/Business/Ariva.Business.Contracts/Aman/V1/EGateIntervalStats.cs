namespace Ariva.Business.Contracts.Aman.V1;

/// <summary>
/// Throughput and reject statistics for one e-gate over one closed one-minute interval, published by AMAN when
/// the interval closes. Every reject becomes demand on a manual lane, so Ariva uses the reject counts to couple
/// e-gate and desk queues. Counts and timings only.
/// </summary>
/// <remarks>
/// Reject reasons are reported as coarse <see cref="EGateRejectCategory"/> values, never as AMAN's detailed
/// security outcome codes. AMAN suppresses small cells: when a category has fewer than 3 rejects in the interval
/// it is added to <see cref="EGateRejectCategory.Other"/>, so a single traveller's outcome cannot be inferred.
/// </remarks>
/// <param name="SiteCode">AMAN site code.</param>
/// <param name="GateCode">AMAN e-gate code within the site.</param>
/// <param name="IntervalStartUtc">Start of the interval in UTC, aligned to the minute.</param>
/// <param name="IntervalSeconds">Interval length in seconds. V1 publishes 60; consumers must reject other values.</param>
/// <param name="Attempts">Number of gate transactions started within the interval.</param>
/// <param name="Accepted">Number of attempts that cleared the gate.</param>
/// <param name="Rejected">Number of attempts sent to a manual desk.</param>
/// <param name="RejectsByCategory">Reject counts by coarse category after small-cell suppression; the values sum to <paramref name="Rejected"/>.</param>
/// <param name="MeanCycleSeconds">Mean gate cycle time per attempt, in seconds.</param>
/// <param name="SourceEventId">Unique id of this publication, for idempotent consumption.</param>
public sealed record EGateIntervalStats(
    string SiteCode,
    string GateCode,
    DateTimeOffset IntervalStartUtc,
    int IntervalSeconds,
    int Attempts,
    int Accepted,
    int Rejected,
    IReadOnlyDictionary<EGateRejectCategory, int> RejectsByCategory,
    double MeanCycleSeconds,
    string SourceEventId);

/// <summary>Coarse e-gate reject categories shared between AMAN and Ariva. Values are fixed; add new ones only at the end.</summary>
public enum EGateRejectCategory
{
    /// <summary>Unknown or suppressed small cells.</summary>
    Other = 0,
    /// <summary>Document could not be read or was not supported by the gate.</summary>
    DocumentRead = 1,
    /// <summary>Face or fingerprint capture or match did not complete at the gate.</summary>
    BiometricCapture = 2,
    /// <summary>Traveller not eligible for the gate (category, age, visa type).</summary>
    Eligibility = 3,
    /// <summary>Sent to an officer for a reason the gate does not disclose.</summary>
    ReferredToOfficer = 4,
    /// <summary>Gate fault or timeout.</summary>
    Technical = 5
}
