namespace Ariva.Core.Services.Reports;

/// <summary>
/// ARV-291: a scheduled delivery's report read failed in the database (<see cref="ISvcReportDeliveries.DeliverAsync"/>), so its
/// transaction may only be rolled back; the delivery round then counts the attempt in a transaction of its own
/// (<see cref="ISvcReportDeliveries.CountFailedReadAsync"/>). The database's own exception is the inner one.
/// </summary>
public sealed class ReportReadFailedException(Guid deliveryId, Exception inner)
    : Exception("The report of a scheduled delivery could not be read from the database.", inner)
{
    /// <summary>The delivery whose read failed.</summary>
    public Guid DeliveryId { get; } = deliveryId;
}
