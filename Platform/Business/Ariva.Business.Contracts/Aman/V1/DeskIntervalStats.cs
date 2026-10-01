namespace Ariva.Business.Contracts.Aman.V1;

/// <summary>
/// Processing statistics for one border desk over one closed one-minute interval, published by AMAN when the
/// interval closes. Counts and timings only: per passenger and per officer records stay in AMAN.
/// </summary>
/// <param name="SiteCode">AMAN site code.</param>
/// <param name="DeskCode">AMAN desk code within the site.</param>
/// <param name="IntervalStartUtc">Start of the interval in UTC, aligned to the minute.</param>
/// <param name="IntervalSeconds">Interval length in seconds. V1 publishes 60; consumers must reject other values.</param>
/// <param name="TransactionsProcessed">Approaches completed at the desk (a family of four processed together is one transaction).</param>
/// <param name="DocumentsProcessed">Travel documents processed at the desk (the same family is four documents).</param>
/// <param name="MeanServiceSeconds">Mean time from transaction start to transaction end, in seconds.</param>
/// <param name="P90ServiceSeconds">90th percentile service time, in seconds.</param>
/// <param name="MeanCycleSeconds">Mean time between consecutive transaction starts while the desk was open, in seconds. Sets throughput; service time alone overstates it.</param>
/// <param name="LaneCategory">Lane category the desk served during the interval, as an AMAN code mapped to Ariva lane categories (for example CIT, RES, VIS, CRW).</param>
/// <param name="SourceEventId">Unique id of this publication, for idempotent consumption.</param>
public sealed record DeskIntervalStats(
    string SiteCode,
    string DeskCode,
    DateTimeOffset IntervalStartUtc,
    int IntervalSeconds,
    int TransactionsProcessed,
    int DocumentsProcessed,
    double MeanServiceSeconds,
    double P90ServiceSeconds,
    double MeanCycleSeconds,
    string LaneCategory,
    string SourceEventId);
