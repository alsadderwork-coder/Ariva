using Ariva.Core.Availability;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Availability;

/// <summary>
/// ARV-118: operating hours as "HH:MM" intervals of a weekday (overnight past midnight, 24:00 for the end of the day), the
/// week's rules (at most four intervals a day, none overlapping on the week's circle) and the stored JSON, read back
/// strictly (CWE-501).
/// </summary>
public sealed class OperatingHoursTests
{
    [Theory]
    [InlineData("06:00", "22:00", 360, 960, false, "22:00")]
    [InlineData("00:00", "24:00", 0, 1_440, false, "24:00")]
    [InlineData("18:00", "02:00", 1_080, 480, true, "02:00")]
    [InlineData("23:59", "00:00", 1_439, 1, false, "24:00")]
    [InlineData("12:00", "11:59", 720, 1_439, true, "11:59")]
    public void TryParse_Should_ReadTheInterval_When_TheTextsAreValid(string opens, string closes, int opensMinute, int length, bool overnight, string shown)
    {
        OperatingInterval.TryParse(opens, closes, out var interval).Should().BeTrue();

        interval.OpensMinute.Should().Be(opensMinute);
        interval.LengthMinutes.Should().Be(length);
        interval.Overnight.Should().Be(overnight);
        interval.Opens.Should().Be(opens);
        interval.Closes.Should().Be(shown, "midnight at the end of the day reads 24:00");
    }

    [Theory]
    [InlineData("06:00", "06:00")]
    [InlineData("00:00", "00:00")]
    [InlineData("24:00", "06:00")]
    [InlineData("6:00", "22:00")]
    [InlineData("06:00", "22:0")]
    [InlineData("06:00:00", "22:00")]
    [InlineData("25:00", "22:00")]
    [InlineData("06:60", "22:00")]
    [InlineData("06:00", "24:01")]
    [InlineData("０6:00", "22:00")]
    [InlineData("06-00", "22:00")]
    [InlineData(null, "22:00")]
    [InlineData("06:00", null)]
    [InlineData("06:00' OR '1'='1", "22:00")]
    public void TryParse_Should_Refuse_When_ATextIsNotAnHourAndMinute(string opens, string closes)
    {
        OperatingInterval.TryParse(opens, closes, out _).Should().BeFalse();
    }

    [Fact]
    public void Covers_Should_SplitAnOvernightIntervalBetweenItsTwoDays_When_ItRunsPastMidnight()
    {
        OperatingInterval.TryParse("22:00", "02:00", out var interval).Should().BeTrue();

        interval.CoversSameDay(1_319).Should().BeFalse();
        interval.CoversSameDay(1_320).Should().BeTrue();
        interval.CoversSameDay(1_439).Should().BeTrue();
        interval.CoversNextDay(0).Should().BeTrue();
        interval.CoversNextDay(119).Should().BeTrue();
        interval.CoversNextDay(120).Should().BeFalse();
    }

    [Fact]
    public void Parse_Should_RefuseOverlaps_When_SundayRunsIntoMonday()
    {
        var (_, problem) = WeeklyHours.Parse([new("Sunday", "22:00", "02:00"), new("Monday", "01:00", "05:00")]);

        problem.Should().Be(WeeklyHours.Overlapping);
        WeeklyHours.Parse([new("Sunday", "22:00", "02:00"), new("Monday", "02:00", "05:00")]).Problem.Should().BeNull("touching is not overlapping");
    }

    [Fact]
    public void Parse_Should_RefuseOverlaps_When_TwoIntervalsOfADayMeet()
    {
        WeeklyHours.Parse([new("Monday", "06:00", "12:00"), new("Monday", "11:00", "13:00")]).Problem.Should().Be(WeeklyHours.Overlapping);
        WeeklyHours.Parse([new("Monday", "06:00", "12:00"), new("Monday", "06:00", "12:00")]).Problem.Should().Be(WeeklyHours.Overlapping);
        WeeklyHours.Parse([new("Monday", "00:00", "24:00"), new("Tuesday", "00:00", "24:00")]).Problem.Should().BeNull();
    }

    [Fact]
    public void Parse_Should_AllowFourIntervalsADay_When_TheFifthIsRefused()
    {
        var four = new[] { ("00:00", "01:00"), ("02:00", "03:00"), ("04:00", "05:00"), ("06:00", "07:00") }
            .Select(i => new OperatingDayHours("Monday", i.Item1, i.Item2)).ToList();

        WeeklyHours.Parse(four).Problem.Should().BeNull();
        WeeklyHours.Parse([.. four, new("Monday", "08:00", "09:00")]).Problem.Should().Be(WeeklyHours.TooManyIntervals);
        WeeklyHours.ParseDay([.. four.Select(i => new OperatingHoursText(i.Opens, i.Closes)), new OperatingHoursText("08:00", "09:00")]).Problem
            .Should().Be(WeeklyHours.TooManyIntervals);
    }

    [Theory]
    [InlineData("monday")]
    [InlineData("MONDAY")]
    [InlineData("Mon")]
    [InlineData("1")]
    [InlineData("Monday ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<script>alert(1)</script>")]
    public void Parse_Should_RefuseTheDay_When_ItIsNotAnEnglishWeekdayName(string day)
    {
        WeeklyHours.Parse([new(day, "06:00", "22:00")]).Problem.Should().Be(WeeklyHours.InvalidDay);
    }

    [Fact]
    public void Parse_Should_GiveAClosedWeek_When_ThereAreNoIntervals()
    {
        var (hours, problem) = WeeklyHours.Parse([]);

        problem.Should().BeNull();
        WeeklyHours.Days.Should().OnlyContain(d => hours.On(d).Count == 0);
    }

    [Fact]
    public void ToJson_Should_RoundTrip_When_TheWeekIsValid()
    {
        var (hours, _) = WeeklyHours.Parse([new("Sunday", "20:00", "03:00"), new("Monday", "06:00", "22:00"), new("Monday", "05:00", "05:30")]);

        var json = hours.ToJson();
        var back = WeeklyHours.TryFromJson(json);

        json.Should().Be("""[{"day":"Monday","opens":"05:00","closes":"05:30"},{"day":"Monday","opens":"06:00","closes":"22:00"},{"day":"Sunday","opens":"20:00","closes":"03:00"}]""");
        back.Should().NotBeNull();
        back.ToJson().Should().Be(json);
        WeeklyHours.TryDayFromJson(WeeklyHours.DayToJson(hours.On(DayOfWeek.Monday))).Should().Equal(hours.On(DayOfWeek.Monday));
        WeeklyHours.TryDayFromJson("[]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[{\"day\":\"Funday\",\"opens\":\"06:00\",\"closes\":\"22:00\"}]")]
    [InlineData("[{\"day\":\"Monday\",\"opens\":\"06:00\",\"closes\":\"06:00\"}]")]
    [InlineData("[{\"day\":\"Monday\",\"opens\":\"06:00\",\"closes\":\"22:00\"},{\"day\":\"Monday\",\"opens\":\"07:00\",\"closes\":\"08:00\"}]")]
    [InlineData("[[[[[[[[[[]]]]]]]]]]")]
    [InlineData("[{\"day\":\"Monday\",\"opens\":\"06:00\",\"closes\":\"22:00\"}")]
    public void TryFromJson_Should_GiveNull_When_TheStoredTextIsNotOneTheServiceWrites(string json)
    {
        WeeklyHours.TryFromJson(json).Should().BeNull();
    }

    [Fact]
    public void TryFromJson_Should_GiveNull_When_TheStoredTextIsTooLong()
    {
        WeeklyHours.TryFromJson("[" + new string(' ', WeeklyHours.MaxJsonLength) + "]").Should().BeNull();
        WeeklyHours.TryDayFromJson("[" + new string(' ', WeeklyHours.MaxJsonLength) + "]").Should().BeNull();
        WeeklyHours.TryDayFromJson("[{\"opens\":\"06:00\",\"closes\":\"06:00\"}]").Should().BeNull();
    }
}
