using Ariva.Di;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using FluentAssertions;
using Npgsql;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-159: the pre-pilot check of stored names. Its flag never reaches configuration; its connection asks for read-only
/// transactions; what it prints is escaped, so a right-to-left override or a tag character in a stored name cannot reorder
/// or hide anything on the operator's terminal. Invisible characters here are escapes.
/// </summary>
public sealed class StoredNameCheckTests
{
    [Theory]
    [InlineData("Snake A", "Snake A")]
    [InlineData("Snake \x202EA", "Snake \\u202EA")]
    [InlineData("Snake \U000E0041", "Snake \\U000E0041")]
    [InlineData("Hall\x0007\n\t", "Hall\\u0007\\u000A\\u0009")]
    [InlineData("\x0635\x0627\x0644\x0629", "\\u0635\\u0627\\u0644\\u0629")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("", "")]
    public void Escape_Should_KeepPrintableAsciiAndWriteEveryOtherCodePointAsAnEscape_When_AStoredNameIsPrinted(string stored, string printed)
    {
        StoredNameCheck.Escape(stored).Should().Be(printed);
        StoredNameCheck.Escape(stored).Should().MatchRegex("^[ -~]*$", "only printable ASCII reaches the terminal");
    }

    [Fact]
    public void Escape_Should_WriteALoneSurrogateAsItself_When_ItIsAnywhere()
    {
        // Built here: theory data is serialised, which would turn a lone surrogate into a replacement character.
        StoredNameCheck.Escape("A" + '\xD83D').Should().Be("A\\uD83D");
        StoredNameCheck.Escape("A" + '\xDE00' + "B").Should().Be("A\\uDE00B");
    }

    [Fact]
    public void ReadOnlyConnectionString_Should_AskForReadOnlyTransactions_When_Built()
    {
        var builder = new NpgsqlConnectionStringBuilder(StoredNameCheck.ReadOnlyConnectionString(new DatabaseSettings { Username = "ariva_app", Password = "p" }));

        builder.Options.Should().Be("-c default_transaction_read_only=on");
        builder.ApplicationName.Should().Be("ariva-stored-name-check");
        builder.Username.Should().Be("ariva_app", "the hosts' runtime login, never the migration login");
    }

    [Fact]
    public void Sql_Should_OnlySelect_When_Read()
    {
        var sql = StoredNameCheck.Sql.ToUpperInvariant();

        sql.TrimStart().Should().StartWith("SELECT");
        foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "ALTER", "DROP", "CREATE", "GRANT", "COPY", "SET" })
            System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\b{verb}\b", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
                .Should().BeFalse(verb);
        sql.Should().NotContain(";", "one statement");
        foreach (var place in new[] { "'SITE.NAME'", "'AIRPORT.NAME'", "'TERMINAL.NAME'", "'LEVEL.NAME'", "'CHECKPOINT.NAME'", "'DESK.NAME'", "'ZONE_PROFILE.NAME'",
                     "'ZONE.NAME'", "'LINE.NAME'", "'DEVICE.QUEUE_ZONE_NAME'" })
            sql.Should().Contain(place);
    }

    [Fact]
    public void Command_Should_TakeItsFlagOutOfTheArguments_When_Requested()
    {
        string[] args = ["--environment=vm-local", StoredNameCheckCommand.Flag];

        StoredNameCheckCommand.IsRequested(args).Should().BeTrue();
        StoredNameCheckCommand.IsRequested(["--migrate"]).Should().BeFalse();
        StoredNameCheckCommand.IsRequested(null).Should().BeFalse();
        StoredNameCheckCommand.WithoutFlag(args).Should().Equal("--environment=vm-local");
    }

    [Fact]
    public void Format_Should_PrintOneEscapedLinePerFindingAndASummary_When_SomeNamesBreakTheRule()
    {
        var id = Guid.Parse("0199a000-0000-7000-8000-000000000159");
        var result = new StoredNameCheckResult(12,
        [
            new StoredNameFinding("zone.name", "DMO", id, false, StoredNameCheck.Escape("Snake \x202EA")),
            new StoredNameFinding("airport.name", null, id, true, StoredNameCheck.Escape("Demo\U000E0041"))
        ]);

        var lines = StoredNameCheckCommand.Format(result).Split(Environment.NewLine);

        lines.Should().Equal(
            "zone.name\tDMO\t0199a000-0000-7000-8000-000000000159\tlive\tSnake \\u202EA",
            "airport.name\t-\t0199a000-0000-7000-8000-000000000159\tdeleted\tDemo\\U000E0041",
            "12 stored names checked; 2 hold a control, invisible format or broken character (ARV-159).",
            "");
        StoredNameCheckCommand.Format(new StoredNameCheckResult(5, [])).Should().StartWith("5 stored names checked; 0 hold");
    }
}
