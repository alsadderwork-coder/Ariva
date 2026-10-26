using Ariva.Testing;
using FluentAssertions;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// ARV-251, as Ariva.UnitTests' TheoryRowsTests (which proves the counting and the refusals on sample theories): this project's
/// row file (theory-rows.json, beside the test executable) gives each theory xUnit lists once its number of rows, which the
/// count guard holds every run to; this test keeps the file current from the theories' own data through the same
/// <see cref="TheoryRows"/> source, linked into this project. No container: it reads the assembly only.
/// </summary>
public sealed class TheoryRowsTests
{
    [Fact]
    public async Task RowFile_Should_HoldEachTheorysRows_When_ComparedWithItsData()
    {
        var problems = await TheoryRows.ProblemsAsync(typeof(TheoryRowsTests).Assembly, TestContext.Current.CancellationToken);

        problems.Should().BeEmpty("the row file gives each theory listed once its rows (change the entry with the theory's data), and no test names itself");
    }
}
