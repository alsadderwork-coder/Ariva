namespace Ariva.Core.Domain.Criteria;

/// <summary>Which local days of a site's availability ledger to read: [From, To] inclusive, at most <see cref="MaxDays"/> days.</summary>
public sealed record AvailabilityCriteria(string From, string To)
{
    /// <summary>The longest range one request reads: a quarter (92 days, about 132,000 ledger minutes).</summary>
    public const int MaxDays = 92;

    public const string InvalidRange = "The range is from and to as local dates yyyy-MM-dd of the site, from not after to, at most 92 days.";

    public static ISpecification<AvailabilityCriteria> Rules() =>
        Fx.Specification<AvailabilityCriteria>()
            .And(c => Bounds(c) is not null, InvalidRange);

    /// <summary>The parsed range when valid, otherwise null.</summary>
    public static (DateOnly From, DateOnly To)? Bounds(AvailabilityCriteria criteria)
    {
        if (criteria is null || !CalendarDates.TryParse(criteria.From, out var from) || !CalendarDates.TryParse(criteria.To, out var to))
            return null;
        return from <= to && to.DayNumber - from.DayNumber < MaxDays ? (from, to) : null;
    }
}
