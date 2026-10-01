namespace Ariva.Core.Domain.Criteria;

/// <summary>
/// Base for search criteria used with Fluentx <c>Search&lt;TCriteria&gt;</c>. Ported from AMAN, with a range check:
/// both bounds are UTC and the range is ordered.
/// </summary>
public record BaseCriteria
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    /// <summary>The rules every criteria object shares; derived criteria add their own with <c>And</c>.</summary>
    public static ISpecification<TCriteria> DateRangeRules<TCriteria>() where TCriteria : BaseCriteria =>
        Fx.Specification<TCriteria>()
            .And(x => !x.FromDate.HasValue || x.FromDate.Value.Kind == DateTimeKind.Utc, "FromDate must be UTC.")
            .And(x => !x.ToDate.HasValue || x.ToDate.Value.Kind == DateTimeKind.Utc, "ToDate must be UTC.")
            .And(x => !x.FromDate.HasValue || !x.ToDate.HasValue || x.FromDate.Value <= x.ToDate.Value, "FromDate must not be after ToDate.");
}
