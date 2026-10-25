using Ariva.Core.Domain.Components;
using Ariva.Di;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using FluentAssertions;
using Npgsql;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-159, widened by ARV-162: the pre-pilot check of stored texts. Its flag never reaches configuration; its connection
/// asks for read-only transactions; its one statement only selects, and leaves the validation result revisions to their
/// service; every value is judged by the rule its entity applies (a name, one line, line breaks allowed, a person's name, a
/// list, a display's JSON, a zone outline); what it prints is escaped (<see cref="DisplayText.Escape"/>), so an override or a
/// tag character in a stored text cannot reorder or hide anything on the operator's terminal. Invisible characters here are
/// escapes.
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
    public void Escape_Should_KeepPrintableAsciiAndWriteEveryOtherCodePointAsAnEscape_When_AStoredTextIsPrinted(string stored, string printed)
    {
        DisplayText.Escape(stored).Should().Be(printed);
        DisplayText.Escape(stored).Should().MatchRegex("^[ -~]*$", "only printable ASCII reaches the terminal");
    }

    [Fact]
    public void Escape_Should_WriteALoneSurrogateAsItself_When_ItIsAnywhere()
    {
        // Built here: theory data is serialised, which would turn a lone surrogate into a replacement character.
        DisplayText.Escape("A" + '\xD83D').Should().Be("A\\uD83D");
        DisplayText.Escape("A" + '\xDE00' + "B").Should().Be("A\\uDE00B");
        DisplayText.Escape(null).Should().BeEmpty();
    }

    public static TheoryData<string, string, string, string[]> Values => new()
    {
        { StoredNameCheck.NameRule, "Snake A", null, [] },
        { StoredNameCheck.NameRule, "Snake \x202EA", null, ["Snake \\u202EA"] },
        { StoredNameCheck.NameRule, "Snake\x200DA", null, ["Snake\\u200DA"] },
        { StoredNameCheck.TextRule, "Xovis PC2", null, [] },
        { StoredNameCheck.TextRule, "PC2\U000E0041", null, ["PC2\\U000E0041"] },
        { StoredNameCheck.TextRule, "Line one\nLine two", null, ["Line one\\u000ALine two"] },
        { StoredNameCheck.NoteRule, "Desks 4 and 5 opened.\nQueue back under 20 minutes.", null, [] },
        { StoredNameCheck.NoteRule, "Done\x202E evil", null, ["Done\\u202E evil"] },
        { StoredNameCheck.PersonRule, "\x0645\x06CC\x200C\x062E\x0648\x0627\x0647\x0645", null, [] },
        { StoredNameCheck.PersonRule, "Amal \x202EHaddad", null, ["Amal \\u202EHaddad"] },
        // ARV-169: the England flag emoji is a name's; a joiner between the waving flag and its tags is not.
        { StoredNameCheck.PersonRule, "Amal \U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F", null, [] },
        { StoredNameCheck.PersonRule, "Amal \U0001F3F4\x200D\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F", null,
            ["Amal \\U0001F3F4\\u200D\\U000E0067\\U000E0062\\U000E0065\\U000E006E\\U000E0067\\U000E007F"] },
        { StoredNameCheck.ListRule, "Snake A\nSnake B", null, [] },
        { StoredNameCheck.ListRule, "Snake A\nSnake\x200BB\nSnake \U000E0041", null, ["Snake\\u200BB", "Snake \\U000E0041"] },
        { StoredNameCheck.EntriesRule, """[{"zone":"Snake A","labels":{"en":"Citizens","ar":"\u0645\u0648\u0627\u0637\u0646"}}]""", null, [] },
        { StoredNameCheck.EntriesRule, """[{"zone":"Snake A","labels":{"en":"Cit\u202Ezens"}},{"zone":"Snake\u200BB","labels":{}}]""", null, ["Cit\\u202Ezens", "Snake\\u200BB"] },
        { StoredNameCheck.EntriesRule, "not json", null, [StoredNameCheck.Unreadable] },
        { StoredNameCheck.MessagesRule, """{"en":"Ask a member of staff"}""", null, [] },
        { StoredNameCheck.MessagesRule, """{"en":"Ask\u0007","ar":"\u0627\u0633\u0623\u0644"}""", null, ["Ask\\u0007"] },
        { StoredNameCheck.MessagesRule, "[]", null, [StoredNameCheck.Unreadable] },
        { StoredNameCheck.OutlineRule, "10 10,34 10,34 22,10 22", "Snake A", [] },
        { StoredNameCheck.OutlineRule, "0 0,10 10,10 0,0 10", "Bow", ["Bow"] },
        { StoredNameCheck.OutlineRule, "0 0,10 0,10 10,5 0.004,0 10", "Notch", ["Notch"] },
        { StoredNameCheck.OutlineRule, "0 0,10 0,5 0.001", "Sliver", ["Sliver"] },
        { StoredNameCheck.FileRule, "arrivals.svg", null, [] },
        { StoredNameCheck.FileRule, "plan\U000E0041.svg", null, ["plan\\U000E0041.svg"] },
        { StoredNameCheck.FileRule, "../etc/plan.svg", null, ["../etc/plan.svg"] },
        { StoredNameCheck.FileRule, "c:plan.svg", null, ["c:plan.svg"] },
        { StoredNameCheck.FileRule, "a\\b.svg", null, ["a\\\\b.svg"] },
        { StoredNameCheck.OutlineRule, "x", "Snake\x202EA", ["Snake\\u202EA"] }
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Breaches_Should_JudgeEachValueByItsColumnsRuleAndEscapeWhatBreaksIt_When_Read(string rule, string value, string label, string[] expected) =>
        StoredNameCheck.Breaches(rule, value, label).Should().Equal(expected);

    [Fact]
    public void ReadOnlyConnectionString_Should_AskForReadOnlyTransactions_When_Built()
    {
        var builder = new NpgsqlConnectionStringBuilder(StoredNameCheck.ReadOnlyConnectionString(new DatabaseSettings { Username = "ariva_app", Password = "p" }));

        builder.Options.Should().Be("-c default_transaction_read_only=on");
        builder.ApplicationName.Should().Be("ariva-stored-name-check");
        builder.Username.Should().Be("ariva_app", "the hosts' runtime login, never the migration login");
        builder.Pooling.Should().BeFalse("a one-off command keeps no pool (ARV-232)");
    }

    [Fact]
    public void Sql_Should_OnlySelectEveryCoveredColumn_When_Read()
    {
        var sql = StoredNameCheck.Sql.ToUpperInvariant();

        sql.TrimStart().Should().StartWith("SELECT");
        foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "ALTER", "DROP", "CREATE", "GRANT", "COPY", "SET" })
            System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\b{verb}\b", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
                .Should().BeFalse(verb);
        sql.Should().NotContain(";", "one statement");
        sql.Should().NotContain("VALIDATION_RESULT_REVISION", "only the results service reads the frozen documents");
        sql.Should().NotContain("VALIDATION_CAMPAIGN_ZONE").And.NotContain("VALIDATION_CAMPAIGN_LINE", "a campaign's copies are evidence, not read");
        foreach (var place in new[]
                 {
                     "SITE.NAME", "AIRPORT.NAME", "TERMINAL.NAME", "LEVEL.NAME", "CHECKPOINT.NAME", "DESK.NAME", "FLOOR_PLAN.ORIGINAL_FILE_NAME", "ZONE_PROFILE.NAME", "ZONE.NAME", "ZONE.POLYGON",
                     "LINE.NAME", "DEVICE.QUEUE_ZONE_NAME", "DEVICE.MODEL", "DEVICE_CALIBRATION.NOTES", "ALERT_RULE.NAME", "ALERT_RULE.SCOPE_ZONES",
                     "ALERT_RULE.ESCALATION_CONTACT", "ALERT.ACKNOWLEDGED_NOTE", "ALERT.ESCALATED_NOTE", "ALERT.RESOLUTION_NOTE", "DISPLAY.NAME", "DISPLAY.LOCATION",
                     "DISPLAY.ENTRIES", "DISPLAY.FALLBACK_MESSAGES", "INTEGRATION_CLIENT.NAME", "OUTBOUND_ENDPOINT.NAME", "REPORT_SCHEDULE.NAME", "USER.DISPLAY_NAME",
                     "OPERATING_DAY_EXCEPTION.REASON", "MAINTENANCE_WINDOW.REASON", "VALIDATION_CAMPAIGN.NAME", "MANUAL_COUNT.REASON", "DESK_OBSERVATION.REASON"
                 })
            sql.Should().Contain($"'{place}'");
    }

    /// <summary>Every place the statement reads, with the rule its entity applies and whether the text may be personal (review recommendation 4).</summary>
    private static readonly Dictionary<string, (string Rule, bool Personal)> Places = new(StringComparer.Ordinal)
    {
        ["site.name"] = (StoredNameCheck.NameRule, false),
        ["airport.name"] = (StoredNameCheck.NameRule, false),
        ["terminal.name"] = (StoredNameCheck.NameRule, false),
        ["level.name"] = (StoredNameCheck.NameRule, false),
        ["checkpoint.name"] = (StoredNameCheck.NameRule, false),
        ["desk.name"] = (StoredNameCheck.NameRule, false),
        ["floor_plan.original_file_name"] = (StoredNameCheck.FileRule, false),
        ["zone_profile.name"] = (StoredNameCheck.NameRule, false),
        ["zone.name"] = (StoredNameCheck.NameRule, false),
        ["zone.polygon"] = (StoredNameCheck.OutlineRule, false),
        ["line.name"] = (StoredNameCheck.NameRule, false),
        ["device.queue_zone_name"] = (StoredNameCheck.NameRule, false),
        ["device.model"] = (StoredNameCheck.TextRule, false),
        ["device_calibration.notes"] = (StoredNameCheck.NoteRule, true),
        ["alert_rule.name"] = (StoredNameCheck.TextRule, false),
        ["alert_rule.scope_zones"] = (StoredNameCheck.ListRule, false),
        ["alert_rule.escalation_contact"] = (StoredNameCheck.TextRule, true),
        ["alert.acknowledged_note"] = (StoredNameCheck.NoteRule, true),
        ["alert.escalated_note"] = (StoredNameCheck.NoteRule, true),
        ["alert.resolution_note"] = (StoredNameCheck.NoteRule, true),
        ["display.name"] = (StoredNameCheck.TextRule, false),
        ["display.location"] = (StoredNameCheck.TextRule, false),
        ["display.entries"] = (StoredNameCheck.EntriesRule, false),
        ["display.fallback_messages"] = (StoredNameCheck.MessagesRule, false),
        ["integration_client.name"] = (StoredNameCheck.TextRule, false),
        ["outbound_endpoint.name"] = (StoredNameCheck.TextRule, false),
        ["report_schedule.name"] = (StoredNameCheck.TextRule, false),
        ["user.display_name"] = (StoredNameCheck.PersonRule, true),
        ["operating_day_exception.reason"] = (StoredNameCheck.TextRule, false),
        ["maintenance_window.reason"] = (StoredNameCheck.TextRule, false),
        ["validation_campaign.name"] = (StoredNameCheck.TextRule, false),
        ["manual_count.reason"] = (StoredNameCheck.TextRule, true),
        ["desk_observation.reason"] = (StoredNameCheck.TextRule, true)
    };

    [Fact]
    public void Sql_Should_JudgeEveryPlaceByItsEntitysRuleAndHideEveryPersonalText_When_Read()
    {
        // Each branch: the place first, then the rule and the personal flag side by side before the label.
        var branches = StoredNameCheck.Sql.Split("UNION ALL", StringSplitOptions.TrimEntries);
        var read = branches.Select(branch =>
        {
            var place = System.Text.RegularExpressions.Regex.Match(branch, @"SELECT '(?<place>[a-z_.]+)'", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            var rule = System.Text.RegularExpressions.Regex.Match(branch, @"'(?<rule>[a-z]+)'(?: AS rule)?, (?<personal>true|false)(?: AS personal)?,",
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            place.Success.Should().BeTrue(branch);
            rule.Success.Should().BeTrue(branch);
            return (Place: place.Groups["place"].Value, Rule: rule.Groups["rule"].Value, Personal: rule.Groups["personal"].Value == "true");
        }).ToList();

        read.Select(r => r.Place).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Places.Keys);
        foreach (var (place, rule, personal) in read)
        {
            rule.Should().Be(Places[place].Rule, place);
            personal.Should().Be(Places[place].Personal, place);
        }
    }

    [Fact]
    public void Positions_Should_NameTheOffendingCodePointsAndWhereTheyAreButNeverTheText_When_TheTextIsPersonal()
    {
        // A person's name keeps its zero-width non-joiner (Persian); the override at code point 6 is named, the name is not.
        var name = "Mehdi\x202E\x0645\x06CC\x200C\x062E";
        StoredNameCheck.Positions(StoredNameCheck.PersonRule, name).Should().Be("U+202E at 6 (text not shown)");
        // ARV-169: the joiner itself is a name's, the tags it splits from the waving flag are not.
        StoredNameCheck.Positions(StoredNameCheck.PersonRule, "A\U0001F3F4\x200D\U000E0067\U000E007F")
            .Should().Be("U+E0067 at 4, U+E007F at 5 (text not shown)");
        // A note keeps its line breaks; a tag character outside the basic plane counts as one code point.
        StoredNameCheck.Positions(StoredNameCheck.NoteRule, "Desks\nopened\U000E0041 at 9\x0007")
            .Should().Be("U+E0041 at 13, U+0007 at 19 (text not shown)");
        StoredNameCheck.Positions(StoredNameCheck.TextRule, "Call me on\n050").Should().Be("U+000A at 11 (text not shown)", "a contact is one line");
        StoredNameCheck.Positions(StoredNameCheck.PersonRule, new string('n', 201)).Should().Be("(longer than its rule allows; text not shown)");
        StoredNameCheck.Positions(StoredNameCheck.TextRule, "Duty officer" + '\xD83D').Should().Be("U+D83D at 13 (text not shown)", "a lone surrogate by its unit");
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
    public void Format_Should_PrintOneEscapedLinePerFindingAndASummary_When_SomeValuesBreakTheirRule()
    {
        var id = Guid.Parse("0199a000-0000-7000-8000-000000000159");
        var result = new StoredNameCheckResult(12,
        [
            new StoredNameFinding("zone.name", "DMO", id, "draft", DisplayText.Escape("Snake \x202EA")),
            new StoredNameFinding("airport.name", null, id, "deleted", DisplayText.Escape("Demo\U000E0041"))
        ]);

        var lines = StoredNameCheckCommand.Format(result).Split(Environment.NewLine);

        lines.Should().Equal(
            "zone.name\tDMO\t0199a000-0000-7000-8000-000000000159\tdraft\tSnake \\u202EA",
            "airport.name\t-\t0199a000-0000-7000-8000-000000000159\tdeleted\tDemo\\U000E0041",
            "12 stored values checked; 2 hold a control, invisible format or broken character, or an outline that touches itself (ARV-159, ARV-162).",
            "");
        StoredNameCheckCommand.Format(new StoredNameCheckResult(5, [])).Should().StartWith("5 stored values checked; 0 hold");
    }
}
