using System.Reflection;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104a: the validation campaign aggregate and its manual counts. A campaign is planned over a published profile version,
/// its queue zones and the lines the queue engine counts for them, and 1 to 31 local days; it goes Planned, Running, Closed;
/// counts are only for lines in scope and bins that start on a planned day, have ended and ended before the version was
/// retired; a correction is the next revision of one's own count with a reason, never an edit; the only person a campaign or
/// a count names is an Ariva user id.
/// </summary>
public sealed class ValidationCampaignTests
{
    #region Fixtures

    // 2026-10-08 10:00 in Dubai (UTC+4).
    private static readonly DateTime Now = new(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private static readonly Guid Manager = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");
    private static readonly Guid Observer = Guid.Parse("0199a000-0000-7000-8000-0000000000bb");
    private static readonly Guid OtherObserver = Guid.Parse("0199a000-0000-7000-8000-0000000000cc");

    private sealed record Profile(ZoneProfile Version, Zone SnakeA, Zone SnakeB, Zone Overflow, Line EntryA, Line ExitA, Line OverflowEntry, Line CountHall, Line EntryB);

    private static IReadOnlyList<FloorPoint> Rect(double x, double y, double w, double h) => [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    /// <summary>
    /// A published version with two snakes: A with an entry, an exit, an overflow band and its entry line; B with an entry and
    /// an exit; and a count line standing alone (counted by no queue zone).
    /// </summary>
    private static Profile Published()
    {
        var level = new Airport("DMO", null, "Demo", "Asia/Dubai").AddTerminal("T1", "T1", "DMO").AddLevel("L0", "Arrivals", 0, 100, 60);
        level.Id = Guid.CreateVersion7();
        var profile = new ZoneProfile("DMO", "Arrivals");
        var snakeA = profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));
        var entryA = profile.AddLine("Entry A", LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), snakeA);
        var exitA = profile.AddLine("Exit A", LineRole.Exit, level, new FloorPoint(30, 22), new FloorPoint(34, 22), snakeA);
        var overflow = profile.AddZone("Overflow A", ZoneKind.Overflow, level, Rect(10, 4, 24, 6), snakeA);
        var overflowEntry = profile.AddLine("Overflow entry A", LineRole.OverflowEntry, level, new FloorPoint(10, 5), new FloorPoint(10, 9), overflow);
        var countHall = profile.AddLine("Count hall", LineRole.Count, level, new FloorPoint(40, 0), new FloorPoint(40, 40));
        var snakeB = profile.AddZone("Snake B", ZoneKind.Queue, level, Rect(50, 10, 24, 12));
        var entryB = profile.AddLine("Entry B", LineRole.Entry, level, new FloorPoint(50, 12), new FloorPoint(50, 16), snakeB);
        profile.AddLine("Exit B", LineRole.Exit, level, new FloorPoint(70, 22), new FloorPoint(74, 22), snakeB);
        profile.Id = Guid.CreateVersion7();
        foreach (var zone in profile.Zones)
            zone.Id = Guid.CreateVersion7();
        foreach (var line in profile.Lines)
            line.Id = Guid.CreateVersion7();
        profile.Publish(7, new Dictionary<Guid, Level> { [level.Id!.Value] = level }, "admin", Now.AddDays(-10)).Should().BeEmpty();
        return new Profile(profile, snakeA, snakeB, overflow, entryA, exitA, overflowEntry, countHall, entryB);
    }

    private static Guid IdOf(Zone zone) => zone.Id!.Value;

    private static Guid IdOf(Line line) => line.Id!.Value;

    private static ValidationCampaign Plan(Profile p, int? bins = null, int? tracers = null, params DateOnly[] days)
    {
        var campaign = new ValidationCampaign("Pilot week 1", p.Version, [IdOf(p.SnakeA)], [IdOf(p.EntryA), IdOf(p.ExitA), IdOf(p.OverflowEntry)],
            days.Length == 0 ? [Today, Today.AddDays(-1)] : days, Today, bins, tracers, Manager, Now);
        campaign.Id = Guid.CreateVersion7();
        return campaign;
    }

    private static ValidationCampaign Running(Profile p)
    {
        var campaign = Plan(p);
        campaign.Start(Manager, Now, ZoneProfileStatus.Published);
        return campaign;
    }

    private static DateTime Utc(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>A count as read back from the database, written by a statement that did not go through the campaign.</summary>
    private sealed class StoredCount : ManualCount
    {
        public StoredCount(Guid campaignId, Guid observerId)
        {
            CampaignId = campaignId;
            ObserverId = observerId;
            Revision = 1;
        }
    }

    #endregion

    #region Planning

    [Fact]
    public void Constructor_Should_PlanTheCampaign_When_ScopeDaysAndTargetsAreValid()
    {
        var p = Published();

        var campaign = Plan(p);

        campaign.Status.Should().Be(ValidationCampaignStatus.Planned);
        campaign.SiteCode.Should().Be("DMO");
        (campaign.ProfileId, campaign.ProfileVersion, campaign.GeometryHash).Should().Be((p.Version.Id!.Value, 7, p.Version.GeometryHash));
        campaign.PlannedDays.Should().Be("2026-10-07,2026-10-08", "days are stored in ascending order");
        campaign.Days.Should().Equal(Today.AddDays(-1), Today);
        (campaign.TargetBinsPerLine, campaign.TargetTracerRuns, campaign.TargetsPlaceholder).Should().Be((20, 30, true), "placeholders until TC-04");
        (campaign.CreatedById, campaign.CreatedUtc, campaign.StartedUtc, campaign.ClosedUtc).Should().Be((Manager, Now, null, null));
        campaign.Zones.Select(z => (z.ZoneId, z.ZoneName, z.ProfileId)).Should().Equal((IdOf(p.SnakeA), "Snake A", p.Version.Id!.Value));
        campaign.Lines.Select(l => (l.LineName, l.LineRole, l.QueueZoneName)).Should().Equal(
            ("Entry A", LineRole.Entry, "Snake A"), ("Exit A", LineRole.Exit, "Snake A"), ("Overflow entry A", LineRole.OverflowEntry, "Snake A"));
    }

    [Fact]
    public void Constructor_Should_KeepGivenTargets_When_TheKpiAnnexSetsThem()
    {
        var campaign = Plan(Published(), bins: 48, tracers: 0);

        (campaign.TargetBinsPerLine, campaign.TargetTracerRuns, campaign.TargetsPlaceholder).Should().Be((48, 0, false));
    }

    [Fact]
    public void Constructor_Should_MarkPlaceholder_When_OnlyOneTargetIsGiven()
    {
        Plan(Published(), bins: 48).TargetsPlaceholder.Should().BeTrue();
    }

    // ARV-069a: the constructor and AuditSummary were in Stryker's safe mode until the first checkpoint; these kill the
    // mutants that survived once they were measured.

    [Fact]
    public void Constructor_Should_OrderTheZonesByName_When_GivenInAnotherOrder()
    {
        var p = Published();

        var campaign = new ValidationCampaign("Pilot", p.Version, [IdOf(p.SnakeB), IdOf(p.SnakeA)], [], [Today], Today, null, null, Manager, Now);

        campaign.Zones.Select(z => z.ZoneName).Should().Equal("Snake A", "Snake B");
    }

    public static TheoryData<string, Guid, DateOnly[], int?, DateTime> RefusedPlans => new()
    {
        { "an anonymous creator", Guid.Empty, [Today], null, Now },
        { "no planned day", Manager, [], null, Now },
        { "a target out of range", Manager, [Today], 0, Now },
        { "a local time", Manager, [Today], null, DateTime.SpecifyKind(Now, DateTimeKind.Local) }
    };

    [Theory]
    [MemberData(nameof(RefusedPlans))]
    public void Constructor_Should_Refuse_When_AnArgumentBreaksItsRule(string because, Guid createdBy, DateOnly[] days, int? bins, DateTime utcNow)
    {
        var p = Published();

        var plan = () => new ValidationCampaign("Pilot", p.Version, [IdOf(p.SnakeA)], [], days, Today, bins, null, createdBy, utcNow);

        plan.Should().Throw<ArgumentException>(because);
    }

    [Fact]
    public void AuditSummary_Should_GiveEveryFieldAndThePlaceholderMark_When_TheTargetsArePlaceholders()
    {
        Plan(Published()).AuditSummary().Should().Be(
            "site=DMO; name=\"Pilot week 1\"; status=Planned; profileVersion=7; zones=1; lines=3; desks=0; days=2026-10-07,2026-10-08; targets=20/30 (placeholder)");
    }

    [Fact]
    public void AuditSummary_Should_KeepAHostileNameInsideItsQuotes_When_TheNameTriesToAddFields()
    {
        // CWE-117: the name is user text in the audit line. A line break is refused when the campaign is created; a quote
        // and a field separator stay inside the serialised name, so the line keeps its fields.
        var p = Published();
        ValidationCampaign Named(string name) => new(name, p.Version, [IdOf(p.SnakeA)], [IdOf(p.EntryA), IdOf(p.ExitA), IdOf(p.OverflowEntry)],
            [Today, Today.AddDays(-1)], Today, null, null, Manager, Now);

        ((Action)(() => Named("Pilot\nsite=X"))).Should().Throw<ArgumentException>("a line break could start a forged audit line");
        var line = Named("Pilot\"; status=Closed; site=X").AuditSummary();

        line.Should().StartWith("site=DMO; name=\"Pilot\\u0022; status=Closed; site=X\"; status=Planned; ");
        line.Should().EndWith("; targets=20/30 (placeholder)");
    }

    [Fact]
    public void AuditSummary_Should_GiveTheTargetsUnmarked_When_TheKpiAnnexSetsThem()
    {
        Plan(Published(), bins: 48, tracers: 0).AuditSummary().Should().EndWith("; targets=48/0");
    }

    public static TheoryData<string> InvalidScopes => ["no zone", "an overflow band as zone", "a line standing alone", "a line of a zone out of scope", "a repeated zone",
        "a repeated line", "an unknown zone", "an unknown line", "51 zones", "201 lines", "no lists"];

    [Theory]
    [MemberData(nameof(InvalidScopes))]
    public void ScopeProblem_Should_RefuseTheScope_When_ItBreaksARule(string scope)
    {
        var p = Published();
        IReadOnlyCollection<Guid> zones = [IdOf(p.SnakeA)];
        IReadOnlyCollection<Guid> lines = [IdOf(p.EntryA)];
        switch (scope)
        {
            case "no zone": zones = []; break;
            case "an overflow band as zone": zones = [IdOf(p.Overflow)]; lines = []; break;
            case "a line standing alone": lines = [IdOf(p.CountHall)]; break;
            case "a line of a zone out of scope": lines = [IdOf(p.EntryB)]; break;
            case "a repeated zone": zones = [IdOf(p.SnakeA), IdOf(p.SnakeA)]; break;
            case "a repeated line": lines = [IdOf(p.EntryA), IdOf(p.EntryA)]; break;
            case "an unknown zone": zones = [Guid.CreateVersion7()]; break;
            case "an unknown line": lines = [Guid.CreateVersion7()]; break;
            case "51 zones": zones = [.. Enumerable.Range(0, 51).Select(_ => Guid.CreateVersion7())]; break;
            case "201 lines": lines = [.. Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7())]; break;
            case "no lists": zones = null; lines = null; break;
        }

        ValidationCampaign.ScopeProblem(p.Version, zones, lines).Should().Be(ValidationErrors.InvalidScope, scope);
        var plan = () => new ValidationCampaign("x", p.Version, zones, lines, [Today], Today, null, null, Manager, Now);
        plan.Should().Throw<ArgumentException>(scope);
    }

    [Fact]
    public void ScopeProblem_Should_AcceptZonesWithoutLines_When_TheCampaignHasTracersOnly()
    {
        var p = Published();

        ValidationCampaign.ScopeProblem(p.Version, [IdOf(p.SnakeA), IdOf(p.SnakeB)], []).Should().BeNull();
        ValidationCampaign.ScopeProblem(p.Version, [IdOf(p.SnakeA), IdOf(p.SnakeB)], [IdOf(p.EntryB), IdOf(p.OverflowEntry)]).Should().BeNull();
    }

    [Fact]
    public void Constructor_Should_Refuse_When_TheVersionIsADraftOrRetired()
    {
        var p = Published();
        var draft = p.Version.CreateDraft("Next");
        draft.Id = Guid.CreateVersion7();

        var onDraft = () => new ValidationCampaign("x", draft, [], [], [Today], Today, null, null, Manager, Now);
        onDraft.Should().Throw<InvalidOperationException>().WithMessage(ValidationErrors.NotPublished);
        p.Version.Retire(Now);
        var onRetired = () => new ValidationCampaign("x", p.Version, [IdOf(p.SnakeA)], [], [Today], Today, null, null, Manager, Now);
        onRetired.Should().Throw<InvalidOperationException>().WithMessage(ValidationErrors.NotPublished);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "blank")]
    [InlineData("bidi \u202e override", "a right-to-left override")]
    [InlineData("tab\tinside", "a control character")]
    public void Constructor_Should_RefuseTheName_When_ItIsNotCleanText(string name, string because)
    {
        var p = Published();

        var plan = () => new ValidationCampaign(name, p.Version, [IdOf(p.SnakeA)], [], [Today], Today, null, null, Manager, Now);

        plan.Should().Throw<ArgumentException>(because);
    }

    [Theory]
    [InlineData(0, 0, true, "today")]
    [InlineData(-31, 0, true, "31 days back")]
    [InlineData(366, 0, true, "366 days ahead")]
    [InlineData(-32, 0, false, "32 days back")]
    [InlineData(367, 0, false, "367 days ahead")]
    [InlineData(0, 30, true, "31 days")]
    [InlineData(0, 31, false, "32 days")]
    public void AreValidDays_Should_BoundTheDays_When_Planned(int firstOffset, int more, bool valid, string because)
    {
        var days = Enumerable.Range(0, more + 1).Select(i => Today.AddDays(firstOffset + i)).ToList();

        ValidationCampaign.AreValidDays(days, Today).Should().Be(valid, because);
    }

    [Fact]
    public void AreValidDays_Should_Refuse_When_NoneOrARepeatedDay()
    {
        ValidationCampaign.AreValidDays([], Today).Should().BeFalse();
        ValidationCampaign.AreValidDays(null, Today).Should().BeFalse();
        ValidationCampaign.AreValidDays([Today, Today], Today).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(1, 0, true)]
    [InlineData(2976, 1000, true)]
    [InlineData(0, null, false)]
    [InlineData(2977, null, false)]
    [InlineData(null, -1, false)]
    [InlineData(null, 1001, false)]
    public void AreValidTargets_Should_BoundTheTargets_When_Given(int? bins, int? tracers, bool valid)
    {
        ValidationCampaign.AreValidTargets(bins, tracers).Should().Be(valid);
    }

    #endregion

    #region Lifecycle

    [Fact]
    public void Start_Should_RunThePlannedCampaign_When_ItsVersionIsStillPublished()
    {
        var campaign = Plan(Published());

        campaign.Start(Manager, Now.AddMinutes(5), ZoneProfileStatus.Published);

        (campaign.Status, campaign.StartedById, campaign.StartedUtc).Should().Be((ValidationCampaignStatus.Running, Manager, Now.AddMinutes(5)));
        campaign.StartProblem(ZoneProfileStatus.Published).Should().Be(ValidationErrors.NotPlanned, "it is running already");
    }

    [Fact]
    public void Start_Should_Refuse_When_TheVersionWasRetired()
    {
        var campaign = Plan(Published());

        campaign.StartProblem(ZoneProfileStatus.Retired).Should().Be(ValidationErrors.ProfileRetired);
        var start = () => campaign.Start(Manager, Now, ZoneProfileStatus.Retired);
        start.Should().Throw<InvalidOperationException>().WithMessage(ValidationErrors.ProfileRetired);
        campaign.Status.Should().Be(ValidationCampaignStatus.Planned);
    }

    [Fact]
    public void Close_Should_CloseForGood_When_PlannedOrRunning()
    {
        var p = Published();
        var planned = Plan(p);
        var running = Running(p);

        planned.Close(Manager, Now.AddHours(1));
        running.Close(Manager, Now.AddHours(2));

        (planned.Status, planned.StartedUtc, planned.ClosedById, planned.ClosedUtc).Should().Be((ValidationCampaignStatus.Closed, null, Manager, Now.AddHours(1)));
        (running.Status, running.StartedUtc, running.ClosedUtc).Should().Be((ValidationCampaignStatus.Closed, Now, Now.AddHours(2)));
        running.CloseProblem().Should().Be(ValidationErrors.Closed);
        running.StartProblem(ZoneProfileStatus.Published).Should().Be(ValidationErrors.Closed);
        var again = () => running.Close(Manager, Now.AddHours(3));
        again.Should().Throw<InvalidOperationException>();
        running.ClosedUtc.Should().Be(Now.AddHours(2));
    }

    [Fact]
    public void Lifecycle_Should_RefuseAnonymousOrLocalTimes_When_Changed()
    {
        var campaign = Plan(Published());

        var anonymous = () => campaign.Start(Guid.Empty, Now, ZoneProfileStatus.Published);
        var local = () => campaign.Close(Manager, DateTime.SpecifyKind(Now, DateTimeKind.Local));

        anonymous.Should().Throw<ArgumentException>();
        local.Should().Throw<ArgumentException>();
        campaign.Status.Should().Be(ValidationCampaignStatus.Planned);
    }

    #endregion

    #region Capture

    public static TheoryData<string, string> CaptureCases => new()
    {
        // Dubai is UTC+4: the planned days 2026-10-07 and 2026-10-08 are 2026-10-06T20:00Z to 2026-10-08T20:00Z.
        { "line not in scope", ValidationErrors.LineNotInScope },
        { "bin not on a quarter hour", ValidationErrors.InvalidBin },
        { "bin in local time", ValidationErrors.InvalidBin },
        { "bin starts the day before the first planned day", ValidationErrors.BinOutsideCampaign },
        { "bin starts the day after the last planned day", ValidationErrors.BinOutsideCampaign },
        { "bin ends more than a minute from now", ValidationErrors.BinNotEnded },
        { "version retired before the bin ended", ValidationErrors.BinAfterRetirement },
        { "first bin of the first local day", null },
        { "bin ending now", null },
        { "version retired as the bin ended", null }
    };

    [Theory]
    [MemberData(nameof(CaptureCases))]
    public void CaptureProblem_Should_DecideByScopeDayAndTime_When_ACountArrives(string situation, string expected)
    {
        var p = Published();
        var campaign = Running(p);
        var line = IdOf(p.EntryA);
        var bin = Utc(8, 5, 30);
        DateTime? retired = null;
        switch (situation)
        {
            case "line not in scope": line = IdOf(p.EntryB); break;
            case "bin not on a quarter hour": bin = Utc(8, 5, 31); break;
            case "bin in local time": bin = DateTime.SpecifyKind(bin, DateTimeKind.Local); break;
            case "bin starts the day before the first planned day": bin = Utc(6, 19, 45); break;
            case "bin starts the day after the last planned day": bin = Utc(8, 20, 0); break;
            case "bin ends more than a minute from now": bin = Utc(8, 6, 0); break;
            case "version retired before the bin ended": retired = Utc(8, 5, 44); break;
            case "first bin of the first local day": bin = Utc(6, 20, 0); break;
            case "bin ending now": bin = Utc(8, 5, 45); break;
            case "version retired as the bin ended": retired = Utc(8, 5, 45); break;
        }

        campaign.CaptureProblem(line, bin, Observer, Now, Dubai, retired).Should().Be(expected, situation);
    }

    [Fact]
    public void CaptureProblem_Should_AcceptTheBinThatEndsWithinTheClockTolerance_When_TheTabletIsAhead()
    {
        var campaign = Running(Published());
        var line = campaign.Lines[0].LineId;

        // The 05:45 bin ends at 06:00: a tablet that submits it at its own 06:00 while the server's clock is up to a minute
        // behind is accepted; two minutes behind, the server sees a bin that has not ended.
        campaign.CaptureProblem(line, Utc(8, 5, 45), Observer, Now.AddSeconds(-59), Dubai, null).Should().BeNull("it ends 59 s ahead of the server's clock");
        campaign.CaptureProblem(line, Utc(8, 5, 45), Observer, Now.AddSeconds(-61), Dubai, null).Should().Be(ValidationErrors.BinNotEnded);
    }

    [Fact]
    public void CaptureProblem_Should_Refuse_When_TheCampaignIsNotRunning()
    {
        var p = Published();
        var planned = Plan(p);
        var closed = Running(p);
        closed.Close(Manager, Now);

        planned.CaptureProblem(IdOf(p.EntryA), Utc(8, 5, 30), Observer, Now, Dubai, null).Should().Be(ValidationErrors.NotStarted);
        closed.CaptureProblem(IdOf(p.EntryA), Utc(8, 5, 30), Observer, Now, Dubai, null).Should().Be(ValidationErrors.Closed);
        var capture = () => closed.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 1, 0, Now, Dubai, null);
        capture.Should().Throw<InvalidOperationException>().WithMessage(ValidationErrors.Closed);
    }

    [Fact]
    public void CaptureProblem_Should_RefuseTheCreatorAndTheStarter_When_TheyAlsoHoldTheObserverRole()
    {
        // Owner decision 2026-10-08 (separation of duties): an account holding a manager role and the observer role never
        // counts for a campaign it created or started; another manager's campaign it may count for.
        var p = Published();
        var startedByAnother = Plan(p);
        startedByAnother.Id = Guid.CreateVersion7();
        startedByAnother.Start(OtherObserver, Now, ZoneProfileStatus.Published);
        var line = IdOf(p.EntryA);

        startedByAnother.ObserverProblem(Manager).Should().Be(ValidationErrors.OwnCampaign, "it created the campaign");
        startedByAnother.ObserverProblem(OtherObserver).Should().Be(ValidationErrors.OwnCampaign, "it started the campaign");
        startedByAnother.ObserverProblem(Observer).Should().BeNull();
        startedByAnother.ObserverProblem(Guid.Empty).Should().Be(ValidationErrors.NotFound);
        startedByAnother.CaptureProblem(line, Utc(8, 5, 30), Manager, Now, Dubai, null).Should().Be(ValidationErrors.OwnCampaign);
        startedByAnother.CaptureProblem(line, Utc(8, 5, 30), OtherObserver, Now, Dubai, null).Should().Be(ValidationErrors.OwnCampaign);
        startedByAnother.CaptureProblem(line, Utc(8, 5, 30), Observer, Now, Dubai, null).Should().BeNull();
        Plan(p).ObserverProblem(Manager).Should().Be(ValidationErrors.OwnCampaign, "the creator, already while the campaign is planned");

        var byCreator = () => startedByAnother.Capture(line, Utc(8, 5, 30), Manager, 1, 0, Now, Dubai, null);
        byCreator.Should().Throw<InvalidOperationException>().WithMessage(ValidationErrors.OwnCampaign);
        var byStarter = () => startedByAnother.Capture(line, Utc(8, 5, 30), OtherObserver, 1, 0, Now, Dubai, null);
        byStarter.Should().Throw<InvalidOperationException>().WithMessage(ValidationErrors.OwnCampaign);
        ValidationErrors.Forbidden.Should().Contain(ValidationErrors.OwnCampaign, "the API answers 403");
        ValidationErrors.Conflicts.Should().NotContain(ValidationErrors.OwnCampaign);
    }

    [Fact]
    public void CorrectionProblem_Should_RefuseTheCreatorOrStarter_When_ACountOfTheirsExists()
    {
        // A count can only exist for them through a statement that bypasses the service (script 0047 refuses that too); the
        // rule still holds for a correction.
        var p = Published();
        var campaign = Running(p);
        var count = campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 42, 3, Now, Dubai, null);
        count.Id = Guid.CreateVersion7();
        var foreign = new StoredCount(campaign.Id!.Value, Manager) { Id = Guid.CreateVersion7() };

        campaign.CorrectionProblem(count, Observer).Should().BeNull();
        campaign.CorrectionProblem(foreign, Manager).Should().Be(ValidationErrors.OwnCampaign);
        campaign.CorrectionProblem(foreign, Observer).Should().Be(ValidationErrors.NotFound, "another observer's count answers like a missing one");
    }

    [Fact]
    public void Capture_Should_RecordRevisionOne_When_TheBinIsInsideTheCampaign()
    {
        var p = Published();
        var campaign = Running(p);

        var count = campaign.Capture(IdOf(p.OverflowEntry), Utc(8, 5, 30), Observer, 42, 3, Now, Dubai, null, "tablet-07:bin-0530");

        (count.CampaignId, count.SiteCode, count.LineId, count.BinStartUtc, count.ObserverId).Should().Be((campaign.Id!.Value, "DMO", IdOf(p.OverflowEntry), Utc(8, 5, 30), Observer));
        (count.Revision, count.CrossingsIn, count.CrossingsOut, count.Reason, count.CorrectsId, count.RecordedUtc).Should().Be((1, 42, 3, null, null, Now));
        count.IdempotencyKey.Should().Be("tablet-07:bin-0530");
    }

    [Fact]
    public void Capture_Should_Refuse_When_TheCampaignWasNeverSaved()
    {
        var p = Published();
        var campaign = new ValidationCampaign("x", p.Version, [IdOf(p.SnakeA)], [IdOf(p.EntryA)], [Today], Today, null, null, Manager, Now);
        campaign.Start(Manager, Now, ZoneProfileStatus.Published);

        var capture = () => campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 1, 0, Now, Dubai, null);

        capture.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(10_001, 0)]
    [InlineData(0, 10_001)]
    public void Capture_Should_RefuseCrossings_When_OutOfRange(int crossingsIn, int crossingsOut)
    {
        var p = Published();
        var campaign = Running(p);

        var capture = () => campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, crossingsIn, crossingsOut, Now, Dubai, null);

        capture.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Corrections

    [Fact]
    public void Correct_Should_AddTheNextRevisionWithAReason_When_TheObserverCorrectsTheirOwnCount()
    {
        var p = Published();
        var campaign = Running(p);
        var first = campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 42, 3, Now, Dubai, null);
        first.Id = Guid.CreateVersion7();

        var second = campaign.Correct(first, Observer, 44, 3, "  Two missed at the start  ", Now.AddMinutes(2));

        (second.Revision, second.CrossingsIn, second.CrossingsOut, second.Reason, second.CorrectsId).Should().Be((2, 44, 3, "Two missed at the start", first.Id));
        (second.LineId, second.BinStartUtc, second.ObserverId, second.RecordedUtc).Should().Be((first.LineId, first.BinStartUtc, Observer, Now.AddMinutes(2)));
        (first.Revision, first.CrossingsIn, first.Reason).Should().Be((1, 42, null), "the corrected revision never changes");
    }

    [Fact]
    public void CorrectionProblem_Should_HideAnotherObserversCount_When_ItIsCorrected()
    {
        var p = Published();
        var campaign = Running(p);
        var theirs = campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), OtherObserver, 42, 3, Now, Dubai, null);
        theirs.Id = Guid.CreateVersion7();

        campaign.CorrectionProblem(theirs, Observer).Should().Be(ValidationErrors.NotFound);
        campaign.CorrectionProblem(null, Observer).Should().Be(ValidationErrors.NotFound);
        var correct = () => campaign.Correct(theirs, Observer, 1, 1, "mine now", Now);
        correct.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CorrectionProblem_Should_Refuse_When_ClosedOrAnotherCampaignsCount()
    {
        var p = Published();
        var campaign = Running(p);
        var other = Running(p);
        var count = campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 42, 3, Now, Dubai, null);
        count.Id = Guid.CreateVersion7();

        other.CorrectionProblem(count, Observer).Should().Be(ValidationErrors.NotFound);
        campaign.Close(Manager, Now);
        campaign.CorrectionProblem(count, Observer).Should().Be(ValidationErrors.Closed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zero\u200bwidth")]
    public void Correct_Should_RequireAReason_When_ARevisionIsAdded(string reason)
    {
        var p = Published();
        var campaign = Running(p);
        var first = campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 42, 3, Now, Dubai, null);
        first.Id = Guid.CreateVersion7();

        var correct = () => campaign.Correct(first, Observer, 44, 3, reason, Now);

        correct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CorrectionProblem_Should_StopAtTheLastRevision_When_ACountWasCorrectedTooOften()
    {
        var p = Published();
        var campaign = Running(p);
        var count = campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 1, 0, Now, Dubai, null);
        for (var revision = 2; revision <= ManualCount.MaxRevisions; revision++)
        {
            count.Id = Guid.CreateVersion7();
            count = campaign.Correct(count, Observer, revision, 0, "again", Now);
        }

        count.Id = Guid.CreateVersion7();
        count.Revision.Should().Be(ManualCount.MaxRevisions);
        campaign.CorrectionProblem(count, Observer).Should().Be(ValidationErrors.TooManyRevisions);
    }

    #endregion

    #region Manual count rules

    [Theory]
    [InlineData("tablet-07:bin-0530", true)]
    [InlineData("01HZX3K5F8Q", true)]
    [InlineData("a.b_c:d-e", true)]
    [InlineData("1234567", false)]
    [InlineData("-starts-with-hyphen", false)]
    [InlineData("has space here", false)]
    [InlineData("<script>alert(1)</script>", false)]
    [InlineData("' OR '1'='1", false)]
    [InlineData("café-key-123", false)]
    [InlineData(null, false)]
    public void IsIdempotencyKey_Should_AcceptTheIntegrationKeyShape_When_Checked(string key, bool valid)
    {
        ManualCount.IsIdempotencyKey(key).Should().Be(valid);
        ManualCount.IsIdempotencyKey(new string('k', 64)).Should().BeTrue();
        ManualCount.IsIdempotencyKey(new string('k', 65)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(10_000, 10_000, true)]
    [InlineData(null, 0, false)]
    [InlineData(0, null, false)]
    [InlineData(-1, 0, false)]
    [InlineData(0, 10_001, false)]
    public void AreCrossings_Should_RequireBothWithinBounds_When_Checked(int? crossingsIn, int? crossingsOut, bool valid)
    {
        ManualCount.AreCrossings(crossingsIn, crossingsOut).Should().Be(valid);
    }

    [Fact]
    public void IsSameRequest_Should_MatchOnlyTheSameCount_When_ARequestIsResent()
    {
        var p = Published();
        var campaign = Running(p);
        var count = campaign.Capture(IdOf(p.EntryA), Utc(8, 5, 30), Observer, 42, 3, Now, Dubai, null, "tablet-07:bin-0530");

        count.IsSameRequest(campaign.Id!.Value, IdOf(p.EntryA), Utc(8, 5, 30), 42, 3, null, null).Should().BeTrue();
        count.IsSameRequest(campaign.Id!.Value, IdOf(p.EntryA), Utc(8, 5, 30), 43, 3, null, null).Should().BeFalse("other crossings");
        count.IsSameRequest(campaign.Id!.Value, IdOf(p.ExitA), Utc(8, 5, 30), 42, 3, null, null).Should().BeFalse("another line");
        count.IsSameRequest(Guid.CreateVersion7(), IdOf(p.EntryA), Utc(8, 5, 30), 42, 3, null, null).Should().BeFalse("another campaign");
    }

    #endregion

    #region Data boundary

    [Fact]
    public void Properties_Should_NameNoPersonBeyondAnArivaUserId_When_CampaignsAndCountsArePersisted()
    {
        // Data boundary (ARV-104a): the persisted properties of the validation aggregate hold no name of a person, no user
        // name, contact or document; people are Ariva user ids (Guid). Names here are of the campaign, zones and lines.
        var types = new[] { typeof(ValidationCampaign), typeof(ValidationCampaignZone), typeof(ValidationCampaignLine), typeof(ManualCount) };
        var persisted = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.GetSetMethod(nonPublic: true) is not null)
            .Select(property => (Type: t.Name, property.Name, property.PropertyType))).ToList();
        string[] personal = ["UserName", "DisplayName", "FirstName", "LastName", "FullName", "Email", "Phone", "Document", "Passport", "Nationality", "Birth", "Badge", "Staff", "Officer", "Traveller", "Passenger", "Mrz", "Pnr"];

        persisted.Where(p => personal.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase))).Should().BeEmpty();
        persisted.Where(p => p.Name.EndsWith("Name", StringComparison.Ordinal)).Select(p => $"{p.Type}.{p.Name}").Should()
            .BeEquivalentTo("ValidationCampaign.Name", "ValidationCampaignZone.ZoneName", "ValidationCampaignLine.LineName", "ValidationCampaignLine.QueueZoneName");
        persisted.Where(p => p.Name.EndsWith("ById", StringComparison.Ordinal) || p.Name == "ObserverId").Should()
            .OnlyContain(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?), "people are Ariva user ids");
        persisted.Should().NotContain(p => p.Name == "CreatedBy" || p.Name == "ModifiedBy", "no user names from the audit base class");
    }

    #endregion
}
