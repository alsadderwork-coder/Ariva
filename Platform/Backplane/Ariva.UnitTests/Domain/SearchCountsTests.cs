using Ariva.Core.Domain.Constants;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>ARV-096d: a search pages through the matches it counts (10,000) and no further.</summary>
public sealed class SearchCountsTests
{
    [Theory]
    [InlineData(1, 50, false)]
    [InlineData(200, 50, false)]
    [InlineData(201, 50, true)]
    [InlineData(20, 500, false)]
    [InlineData(21, 500, true)]
    [InlineData(10_000, 1, false)]
    [InlineData(10_001, 1, true)]
    [InlineData(int.MaxValue, 500, true)]
    public void StartsBeyondCap_Should_RefuseOnlyPagesThatStartPastTheCount_When_Paging(int pageIndex, int pageSize, bool beyond)
    {
        SearchCounts.StartsBeyondCap(pageIndex, pageSize).Should().Be(beyond);
    }
}
