using Ariva.Core.Domain.Criteria;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Common;

/// <summary>
/// ARV-004: validation with Fluentx specifications, the pattern AMAN services use (Fx.Specification, And with a
/// message, ValidateAllAsync returning a Result), exercised through <see cref="BaseCriteria.DateRangeRules{T}"/>.
/// </summary>
public sealed class SpecificationTests
{
    private static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record SampleCriteria : BaseCriteria;

    [Fact]
    public async Task ValidateAllAsync_Should_Pass_When_RangeIsUtcAndOrdered()
    {
        var criteria = new SampleCriteria { FromDate = From, ToDate = From.AddDays(1) };

        var result = await BaseCriteria.DateRangeRules<SampleCriteria>().ValidateAllAsync(criteria);

        result.HasErrors.Should().Be(false);
    }

    [Fact]
    public async Task ValidateAllAsync_Should_Pass_When_BoundsAreOpen()
    {
        var result = await BaseCriteria.DateRangeRules<SampleCriteria>().ValidateAllAsync(new SampleCriteria());

        result.HasErrors.Should().Be(false);
    }

    [Fact]
    public async Task ValidateAllAsync_Should_Fail_When_RangeIsReversed()
    {
        var criteria = new SampleCriteria { FromDate = From.AddDays(1), ToDate = From };

        var result = await BaseCriteria.DateRangeRules<SampleCriteria>().ValidateAllAsync(criteria);

        result.HasErrors.Should().Be(true);
        result.ErrorMessages.Should().Contain("FromDate must not be after ToDate.");
    }

    [Fact]
    public async Task ValidateAllAsync_Should_ReportEveryBrokenRule_When_SeveralRulesFail()
    {
        var criteria = new SampleCriteria
        {
            FromDate = DateTime.SpecifyKind(From, DateTimeKind.Local),
            ToDate = DateTime.SpecifyKind(From, DateTimeKind.Unspecified)
        };

        var result = await BaseCriteria.DateRangeRules<SampleCriteria>().ValidateAllAsync(criteria);

        result.HasErrors.Should().Be(true);
        result.ErrorMessages.Should().Contain("FromDate must be UTC.").And.Contain("ToDate must be UTC.");
    }
}
