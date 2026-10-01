namespace Ariva.Business.Contracts.Aman.V1;

/// <summary>
/// Expected border demand for one inbound flight, split by lane category, computed by AMAN from advance
/// passenger information. Counts per lane only: no passenger identities, documents or nationalities of
/// individuals cross. AMAN republishes the record when its computation changes; the latest
/// <paramref name="ComputedAtUtc"/> wins.
/// </summary>
/// <param name="SiteCode">AMAN site code of the arrivals hall the flight feeds.</param>
/// <param name="FlightKey">Stable flight key as AMAN formats it (carrier, flight number and scheduled date); Ariva maps it to its canonical flight.</param>
/// <param name="ScheduledArrivalUtc">Scheduled arrival time in UTC.</param>
/// <param name="BoardedTotal">Passengers boarded for this destination. Not always the sum of the lanes (transfers and crew may be excluded from lane demand).</param>
/// <param name="PassengersByLane">Expected manual-lane passenger counts keyed by lane category code (CIT, RES, VIS, CRW).</param>
/// <param name="EGateEligible">Passengers expected to be eligible for e-gates; counted separately from the lanes so Ariva can apply its own uptake and reject rates.</param>
/// <param name="ComputedAtUtc">When AMAN computed these counts, in UTC.</param>
/// <param name="SourceEventId">Unique id of this publication, for idempotent consumption.</param>
public sealed record InboundFlightLaneDemand(
    string SiteCode,
    string FlightKey,
    DateTimeOffset ScheduledArrivalUtc,
    int BoardedTotal,
    IReadOnlyDictionary<string, int> PassengersByLane,
    int EGateEligible,
    DateTimeOffset ComputedAtUtc,
    string SourceEventId);
