using Ariva.Testing;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-251: xUnit lists a theory whose data it cannot serialize once and runs its rows under that one case, so the count guard
/// (scripts/test-count-guard.mjs) holds each such theory to the number of rows this project's row file (theory-rows.json,
/// copied beside the test executable) gives it: a lost row, while another of its rows ran, fails the gate. These tests keep the
/// file current from the theories' own data (<see cref="TheoryRows"/>, shared with Ariva.IntegrationTests) and prove its
/// counting and its refusals on sample theories and texts. A theory listed once that the file leaves out is the guard's to
/// find, once it runs more than one row.
/// </summary>
public sealed class TheoryRowsTests
{
    #region Fields

    private static readonly Func<int> First = () => 1;
    private static readonly Func<int> Second = () => 2;
    private static readonly Func<int> Third = () => 3;

    /// <summary>Rows a run cannot list one by one (a delegate cannot be serialized), from two data attributes.</summary>
    public static TheoryData<Func<int>> TwoRows => [First, Second];

    public static TheoryData<Func<int>> OneRow => [Third];

    private static string SampleName => Name(nameof(Rows_Should_BeCountedOverEveryDataAttribute_When_TheDataCannotBeSerialized));

    #endregion

    #region Tests

    [Fact]
    public async Task RowFile_Should_HoldEachTheorysRows_When_ComparedWithItsData()
    {
        var problems = await TheoryRows.ProblemsAsync(typeof(TheoryRowsTests).Assembly, TestContext.Current.CancellationToken);

        problems.Should().BeEmpty("the row file gives each theory listed once its rows (change the entry with the theory's data), and no test names itself");
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, TheoryRows.FileName)).Should().Contain(SampleName, "this class's own sample theory is listed once");
    }

    [Theory]
    [MemberData(nameof(TwoRows))]
    [MemberData(nameof(OneRow))]
    public void Rows_Should_BeCountedOverEveryDataAttribute_When_TheDataCannotBeSerialized(Func<int> row)
    {
        row().Should().BeInRange(1, 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Rows_Should_BeOnePerInlineData_When_Counted(int row)
    {
        row.Should().BeInRange(1, 2);
    }

    [Theory]
    [ClassData(typeof(ThreeNumbers))]
    public void Rows_Should_BeTheClassDataItems_When_Counted(int row)
    {
        row.Should().BeInRange(1, 3);
    }

    [Fact]
    public void RowsOf_Should_CountEachKindOfDataAttribute_When_AskedForTheSampleTheories()
    {
        var assembly = typeof(TheoryRowsTests).Assembly;

        TheoryRows.RowsOf(TheoryRows.TheoryOf(assembly, SampleName)).Should().Be(3, "two member data attributes of two rows and one");
        TheoryRows.RowsOf(TheoryRows.TheoryOf(assembly, Name(nameof(Rows_Should_BeOnePerInlineData_When_Counted)))).Should().Be(2);
        TheoryRows.RowsOf(TheoryRows.TheoryOf(assembly, Name(nameof(Rows_Should_BeTheClassDataItems_When_Counted)))).Should().Be(3);
        TheoryRows.TheoryOf(assembly, Name(nameof(RowFile_Should_HoldEachTheorysRows_When_ComparedWithItsData))).Should().BeNull("a fact is no theory");
        TheoryRows.TheoryOf(assembly, "Ariva.UnitTests.Architecture.NoSuchTests.Method").Should().BeNull();
    }

    [Theory]
    [InlineData("""{"S": 3}""", 0, null)]
    [InlineData("""{"S": 4}""", 1, "4 rows in the row file, 3 in its data")]
    [InlineData("""{"S": 3, "S": 3}""", 1, "named twice")]
    [InlineData("""{"S": 1}""", 1, "not a whole number from 2 to 100,000")]
    [InlineData("""{"S": 100001}""", 1, "not a whole number from 2 to 100,000")]
    [InlineData("""{"S": 2.5}""", 1, "not a whole number from 2 to 100,000")]
    [InlineData("""{"S": "3"}""", 1, "not a whole number from 2 to 100,000")]
    [InlineData("""{"Ariva.UnitTests.Architecture.TheoryRowsTests.Gone": 3}""", 1, "no theory of that name")]
    [InlineData("""[]""", 1, "not a JSON object")]
    public void FileProblems_Should_RefuseAWrongCountATwiceNamedMethodAndAnUnknownTheory_When_Read(string text, int count, string problem)
    {
        var problems = TheoryRows.FileProblems(typeof(TheoryRowsTests).Assembly, text.Replace("\"S\"", $"\"{SampleName}\"", StringComparison.Ordinal));

        problems.Should().HaveCount(count);
        if (problem is not null)
            problems[0].Should().Contain(problem);
    }

    [Fact]
    public void NameProblems_Should_RefuseADisplayNameOfATestADataAttributeOrARow_When_Set()
    {
        var plain = TheoryRows.NameProblems("T.M", new FactAttribute(), [new InlineDataAttribute(1)], [new TheoryDataRow<int>(1)]);
        var test = TheoryRows.NameProblems("T.M", new TheoryAttribute { DisplayName = "named" }, [], []);
        var data = TheoryRows.NameProblems("T.M", new TheoryAttribute(), [new InlineDataAttribute(1) { TestDisplayName = "named" }], []);
        var row = TheoryRows.NameProblems("T.M", new TheoryAttribute(), [], [new TheoryDataRow<int>(1) { TestDisplayName = "named" }]);

        plain.Should().BeEmpty();
        test.Should().ContainSingle().Which.Should().Contain("a display name of its own");
        data.Should().ContainSingle().Which.Should().Contain("a data attribute names its rows");
        row.Should().ContainSingle().Which.Should().Contain("a data row names itself");
    }

    #endregion

    #region Helpers

    private static string Name(string method) => $"{typeof(TheoryRowsTests).FullName}.{method}";

    public sealed class ThreeNumbers : TheoryData<int>
    {
        public ThreeNumbers() => AddRange(1, 2, 3);
    }

    #endregion
}
