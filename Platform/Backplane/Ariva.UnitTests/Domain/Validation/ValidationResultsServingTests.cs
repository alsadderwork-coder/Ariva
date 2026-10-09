using System.Security.Cryptography;
using System.Text;
using Ariva.Core;
using Ariva.Core.Availability;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Services.Validation;
using Ariva.Core.Validation;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;
using Results = Ariva.Core.Domain.ViewModels.ValidationResultsViewModel;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104g: the validation results as served and frozen. The reader each caller is (from its stored roles: desks to
/// <c>BorderDesks.View</c> holders only, observer-level results every observer's to View or Manage holders and an observer's own
/// to an observer, the shadow's figures to View holders); the per-caller projection (<see cref="Results.For"/>), which never
/// changes the shared results; the stored document (camelCase JSON with enums by name, read back to the same bytes, without the
/// revision and the audience) and its SHA-256 content hash; the zone-days bound at planning (M3 of the ARV-104g2 review); the
/// recomputation's reason rule; and the results settings' bounds.
/// </summary>
public sealed class ValidationResultsServingTests
{
    #region Setup

    private const int Version = 7;
    private const string Vis = "A-VIS";
    private static readonly Guid ZoneA = G(1);
    private static readonly Guid LineA = G(11);
    private static readonly Guid D01 = G(31);
    private static readonly Guid O1 = G(901);
    private static readonly Guid O2 = G(902);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    private static DateTime At(int hour, int minute) => new(2026, 10, 8, hour, minute, 0, DateTimeKind.Utc);

    private static TracerRunRow Run(int n, Guid observer, Guid batch, DateTime joined) =>
        new(G(1000 + n), batch, observer, ZoneA, $"T-{n:D2}", joined, joined.AddMinutes(6), 0, joined, joined.AddMinutes(6), false);

    /// <summary>
    /// A closed campaign's results over one zone, one line and one desk with two observers: queue minutes with the published and the
    /// shadow nowcast, tracer runs of both observers in batches of their own, both observers' desk states, and unusable keys of
    /// every kind the serving rules touch (a manual count and a desk state of each observer, a desk minute, a shadow minute).
    /// </summary>
    private static Results Campaign()
    {
        var minutes = new List<QueueMinuteRow>();
        var shadows = new List<ShadowMinuteRow>();
        for (var m = -5; m <= 65; m++)
        {
            var at = At(18, 0).AddMinutes(m);
            minutes.Add(new QueueMinuteRow(Vis, at, Version, BinStatus.Final, 10, 6 + (m % 3), 5 + (m % 4), null, false));
            shadows.Add(new ShadowMinuteRow(Vis, at, 4 + (m % 5), null, false, null));
        }

        var input = new ComparisonInput
        {
            Scope = new ComparisonScope(Version, [new ScopeZone(ZoneA, Vis)], [new ScopeLine(LineA, "Entry A", LineRole.Entry, Vis)],
                [new UtcWindow(At(18, 0), At(19, 0))]) { Desks = [new ScopeDesk(D01, "IMM", "D01")] },
            QueueMinutes = minutes,
            ShadowMinutes = shadows,
            QueueBins = [.. Enumerable.Range(-1, 12).Select(i => new QueueBinRow(Vis, At(18, 0).AddMinutes(15 * i), TimeSpan.FromMinutes(15), 1, BinStatus.Final,
                BinQuality.Good, Version, 100, 0))],
            TracerRuns = [Run(1, O1, G(2001), At(18, 10)), Run(2, O2, G(2002), At(18, 20))],
            DeskObservations = [.. Enumerable.Range(0, 10).Select(m => new DeskObservationRow(D01, At(18, m), O1, 1, ObservedDeskState.Serving)),
                .. Enumerable.Range(0, 5).Select(m => new DeskObservationRow(D01, At(18, m), O2, 1, ObservedDeskState.Serving))],
            DeskMinutes = [.. Enumerable.Range(0, 10).Select(m => new DeskMinuteRow("IMM", "D01", At(18, m), 0, 0, 60, 0, 0, false))]
        };
        var compared = ValidationComparison.Compare(input);
        compared.Problem.Should().BeNull();
        var keys = new[]
        {
            new UnusableKey(UnusableKeyKind.ManualCount, Vis, "Entry A", At(18, 0), O1, UnusableKeyReason.Refused),
            new UnusableKey(UnusableKeyKind.ManualCount, Vis, "Entry A", At(18, 15), O2, UnusableKeyReason.Refused),
            new UnusableKey(UnusableKeyKind.DeskObservation, null, null, At(18, 20), O1, UnusableKeyReason.Conflicting, D01),
            new UnusableKey(UnusableKeyKind.DeskObservation, null, null, At(18, 21), O2, UnusableKeyReason.Conflicting, D01),
            new UnusableKey(UnusableKeyKind.DeskMinute, null, null, At(18, 22), null, UnusableKeyReason.Refused, D01),
            new UnusableKey(UnusableKeyKind.ShadowMinute, Vis, null, At(18, 30), null, UnusableKeyReason.Refused)
        };
        var result = compared with { LeftOut = compared.LeftOut with { UnusableKeys = keys, ShadowMinutes = 1, ManualCounts = 2 } };
        var facts = new Results.CampaignFacts(G(5000), "DMO", "Pilot <b>week</b>", ValidationCampaignStatus.Closed, Version, new string('c', 64), ["2026-10-08"],
            new CampaignTargets(1, 2, false), HasDesks: true);
        var availability = new Results.AvailabilityView(AvailabilitySummary.PilotTarget, AvailabilityCounts.Zero, []);
        return Results.From(result, new Results.Sources(facts, "Asia/Dubai", At(20, 0), null, null, ShadowRead: false, [], availability,
            [new Results.CalibrationView("CAM-1", Vis, "ManualCountTally", 120, 97.5, 0.4, 95, true, At(10, 0))]));
    }

    private static readonly Results Shared = Campaign();

    private static string Json(Results results) => Encoding.UTF8.GetString(results.ToJson());

    #endregion

    #region Reader

    public static TheoryData<string[], bool, Results.ObserverResults, bool> Roles => new()
    {
        { [RoleCodes.BorderShiftSupervisor], true, Results.ObserverResults.All, true },
        { [RoleCodes.TerminalDutyManager], false, Results.ObserverResults.All, true },
        { [RoleCodes.SystemAdministrator], true, Results.ObserverResults.All, true },
        { [RoleCodes.ValidationObserver], false, Results.ObserverResults.Own, false },
        { [RoleCodes.HandlerStationManager], false, Results.ObserverResults.None, false },
        { [RoleCodes.TerminalDutyManager, RoleCodes.ValidationObserver], false, Results.ObserverResults.All, true },
        { [RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], true, Results.ObserverResults.All, true },
        { [RoleCodes.HandlerStationManager, RoleCodes.ValidationObserver], false, Results.ObserverResults.Own, false },
        { ["NotARole"], false, Results.ObserverResults.None, false },
        { [], false, Results.ObserverResults.None, false }
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public void Reader_Should_FollowTheStoredRoles_When_Built(string[] roles, bool desks, Results.ObserverResults observers, bool proof)
    {
        var reader = Results.Reader.Of(roles, O1);

        (reader.SeesDesks, reader.Observers, reader.SeesProof).Should().Be((desks, observers, proof));
        reader.ObserverId.Should().Be(observers == Results.ObserverResults.Own ? O1 : null, "only an observer's own results need its id");
    }

    [Fact]
    public void Reader_Should_GrantNothing_When_TheRolesOrTheObserverAreMissing()
    {
        Results.Reader.Of(null, O1).Should().Be(Results.Reader.Nobody);
        Results.Reader.Of([null, null], O1).Should().Be(Results.Reader.Nobody);
        // An observer without an Ariva user id cannot be told which results are its own: none.
        Results.Reader.Of([RoleCodes.ValidationObserver], null).Observers.Should().Be(Results.ObserverResults.None);
        Results.Reader.Of([RoleCodes.ValidationObserver], Guid.Empty).Observers.Should().Be(Results.ObserverResults.None);
    }

    #endregion

    #region Projection

    [Fact]
    public void For_Should_ServeEverySection_When_TheReaderIsABorderManager()
    {
        var served = Shared.For(Results.Reader.Of([RoleCodes.BorderShiftSupervisor], O1));

        served.Audience.Should().Be(new Results.AudienceView(true, Results.ObserverResults.All, true));
        served.Desks.Should().BeEquivalentTo(Shared.Desks, "nothing of the desks is withheld from a border manager");
        served.Desks.ObserverKeys.Select(k => k.ObserverId).Should().Equal(O1, O2);
        served.Observers.Should().BeSameAs(Shared.Observers);
        served.Nowcast.Should().BeSameAs(Shared.Nowcast);
        served.LeftOut.ShadowMinutes.Should().Be(1);
    }

    [Fact]
    public void For_Should_WithholdDeskStateResultsAndDeskKeys_When_TheReaderIsOnTheAirportSide()
    {
        var served = Shared.For(Results.Reader.Of([RoleCodes.TerminalDutyManager], O1));
        var json = Json(served);

        served.Desks.Should().BeNull("desk-state results are border per-desk data (data boundary)");
        served.Audience.DesksIncluded.Should().BeFalse();
        served.Criteria.Should().NotContain(c => c.Criterion == CampaignCriterion.DeskStateAgreement);
        json.Should().NotContain("\"desks\":{").And.NotContain("D01").And.NotContain(D01.ToString()).And.NotContain("deskStateAgreement");
        served.LeftOut.Keys.Should().NotContain(k => k.Kind == UnusableKeyKind.DeskObservation || k.Kind == UnusableKeyKind.DeskMinute);
        served.Observers.Should().BeSameAs(Shared.Observers, "a duty manager holds Validation.View");
    }

    [Fact]
    public void For_Should_KeepOnlyTheObserversOwnResults_When_TheReaderIsAnObserver()
    {
        var served = Shared.For(Results.Reader.Of([RoleCodes.ValidationObserver], O2));
        var json = Json(served);

        served.Audience.Should().Be(new Results.AudienceView(false, Results.ObserverResults.Own, false));
        served.Observers.Runs.Should().ContainSingle().Which.ObserverId.Should().Be(O2);
        served.Observers.Offsets.Should().OnlyContain(o => o.ObserverId == O2);
        served.Observers.Batches.Should().OnlyContain(b => b.ObserverId == O2).And.NotBeEmpty();
        served.Observers.CountKeys.Should().ContainSingle().Which.ObserverId.Should().Be(O2);
        json.Should().NotContain(O1.ToString(), "another observer's id never reaches an observer");
        served.Desks.Should().BeNull("a pure observer does not see border desk results");
    }

    [Fact]
    public void For_Should_KeepOnlyTheirOwnDeskKeys_When_ABorderReaderSeesOnlyItsOwnObserverResults()
    {
        var reader = new Results.Reader(SeesDesks: true, Results.ObserverResults.Own, O1, SeesProof: false);

        var served = Shared.For(reader);

        served.Desks.ObserverKeys.Should().ContainSingle().Which.ObserverId.Should().Be(O1);
        served.Desks.Keys.Should().OnlyContain(k => k.ObserverId == null);
    }

    [Fact]
    public void For_Should_WithholdTheShadowsFigures_When_TheReaderDoesNotHoldValidationView()
    {
        var withProblem = Shared with { Review = [.. Shared.Review, CampaignReview.ProofNotRead] };

        var served = withProblem.For(Results.Reader.Of([RoleCodes.HandlerStationManager], O1));

        served.Nowcast.Zones.Should().NotBeEmpty().And.OnlyContain(z => z.Shadow == null && z.Both == null);
        served.Nowcast.Overall.Shadow.Should().BeNull();
        served.Nowcast.Overall.Both.Should().BeNull();
        served.Nowcast.Overall.Published.Should().Be(Shared.Nowcast.Overall.Published, "the published nowcast's error is not the proof");
        served.LeftOut.ShadowMinutes.Should().Be(0);
        served.LeftOut.Keys.Should().NotContain(k => k.Kind == UnusableKeyKind.ShadowMinute);
        served.Review.Should().NotContain(CampaignReview.ProofNotRead);
        served.Observers.Should().BeNull();
        served.Desks.Should().BeNull();
        served.Audience.Should().Be(new Results.AudienceView(false, Results.ObserverResults.None, false));
    }

    [Fact]
    public void For_Should_NeverChangeTheSharedResults_When_EveryReaderIsServed()
    {
        var before = Shared.ToDocument();

        foreach (var reader in new[] { Results.Reader.Nobody, Results.Reader.Of([RoleCodes.ValidationObserver], O1), Results.Reader.Of([RoleCodes.TerminalDutyManager], O1), null })
            _ = Shared.For(reader);

        Shared.ToDocument().Should().Equal(before);
        Shared.For(null).Should().BeEquivalentTo(Shared.For(Results.Reader.Nobody), "no reader is nobody");
    }

    [Fact]
    public void Results_Should_ListNoMinute_When_Built()
    {
        // M3 of the ARV-104g2 review: neither the compared nowcast minutes nor the observed desk minutes are part of the results.
        typeof(Results.NowcastView).GetProperties().Select(p => p.Name).Should().NotContain("Minutes");
        typeof(Results.DesksView).GetProperties().Select(p => p.Name).Should().NotContain("Minutes");
        var json = Json(Shared);
        json.Should().NotContain("minuteUtc").And.NotContain("realisedWaitMinutes");
        Shared.Nowcast.Overall.Published.Minutes.Should().BeGreaterThan(0, "the summaries still count the minutes");
        Shared.Desks.Overall.Minutes.Should().Be(10);
    }

    #endregion

    #region Stored document

    [Fact]
    public void ToDocument_Should_ReadBackToTheSameBytes_When_Stored()
    {
        var document = Shared.ToDocument();

        var read = Results.FromJson(document);

        read.Should().NotBeNull();
        read.ToDocument().Should().Equal(document, "a frozen document read back and written again is the same bytes");
        Results.Hash(document).Should().Be(Convert.ToHexStringLower(SHA256.HashData(document))).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ToDocument_Should_LeaveOutTheRevisionAndTheAudience_When_TheyAreSet()
    {
        var served = Shared.For(Results.Reader.Of([RoleCodes.BorderShiftSupervisor], O1)) with
        {
            Revision = new Results.RevisionView(2, 2, new string('a', 64), At(21, 0), "Late corrections")
        };

        served.ToDocument().Should().Equal(Shared.ToDocument(), "the hash covers the results, never who reads them or which revision they are");
        var json = Json(served);
        json.Should().Contain("\"revision\":{\"number\":2,\"revisions\":2").And.Contain("\"audience\":{\"desksIncluded\":true,\"observers\":\"All\"");
        Encoding.UTF8.GetString(Shared.ToDocument()).Should().Contain("\"revision\":null,\"audience\":null");
    }

    [Fact]
    public void ToDocument_Should_WriteEnumsByNameAndMarkupAsInertText_When_Stored()
    {
        var json = Encoding.UTF8.GetString(Shared.ToDocument());

        json.Should().Contain("\"criterion\":\"CountAccuracy\"").And.Contain("\"exclusions\":\"Strict\"");
        json.Should().Contain("Pilot \\u003Cb\\u003Eweek\\u003C/b\\u003E", "markup in a campaign name is escaped JSON text (CWE-79)");
        json.Should().NotContain("<b>");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"criteria\":\"not a list\"}")]
    [InlineData("{\"x\":1}")]
    [InlineData("{\"campaignId\":\"00000000-0000-0000-0000-000000000000\",\"siteCode\":\"DMO\",\"criteria\":[]}")]
    public void FromJson_Should_GiveNothing_When_TheBytesAreNotResults(string text) =>
        Results.FromJson(Encoding.UTF8.GetBytes(text)).Should().BeNull();

    [Fact]
    public void FromJson_Should_RefuseADocumentDeeperThanTheBound_When_Read()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 70)) + "1" + new string('}', 70);

        Results.FromJson(Encoding.UTF8.GetBytes(deep)).Should().BeNull("a document is at most 64 levels deep (CWE-120)");
    }

    #endregion

    #region Planning bound and requests

    [Theory]
    [InlineData(50, 8, true)]
    [InlineData(12, 31, true)]
    [InlineData(1, 31, true)]
    [InlineData(20, 20, true)]
    [InlineData(40, 10, true)]
    [InlineData(50, 9, false)]
    [InlineData(13, 31, false)]
    [InlineData(401, 1, false)]
    public void IsWithinZoneDays_Should_AllowAtMost400ZoneDays_When_Planned(int zones, int days, bool within) =>
        ValidationCampaign.IsWithinZoneDays(zones, days).Should().Be(within);

    [Fact]
    public async Task Create_Should_RefuseACampaignBeyondTheZoneDays_When_Planned()
    {
        var today = new DateOnly(2026, 10, 1);
        var days = Enumerable.Range(0, 31).Select(d => today.AddDays(d).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var zones = Enumerable.Range(0, 13).Select(i => G(100 + i)).ToList();
        var request = new CreateValidationCampaignRequest("Month", 1, zones, [], days, null, null);

        var refused = await ValidationRules.Create(today).ValidateAllAsync(request);
        var allowed = await ValidationRules.Create(today).ValidateAllAsync(request with { ZoneIds = zones.Take(12).ToList() });

        refused.ErrorMessages.Should().Equal(ValidationErrors.TooManyZoneDays);
        allowed.HasErrors.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Late desk corrections", true)]
    [InlineData("tab\there", false)]
    [InlineData("zero\u200Bwidth", false)]
    public async Task Recompute_Should_NeedAClean1To500CharacterReason_When_Asked(string reason, bool valid)
    {
        var result = await ValidationRules.Recompute().ValidateAllAsync(new RecomputeValidationResultsRequest(reason));

        result.HasErrors.Should().Be(!valid);
        if (!valid)
            result.ErrorMessages.Should().Equal(ValidationResultsErrors.InvalidReason);
        (await ValidationRules.Recompute().ValidateAllAsync(new RecomputeValidationResultsRequest(new string('r', 500)))).HasErrors.Should().BeFalse();
        (await ValidationRules.Recompute().ValidateAllAsync(new RecomputeValidationResultsRequest(new string('r', 501)))).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Errors_Should_SortEveryAnswerIntoOneStatus_When_Mapped()
    {
        ValidationResultsErrors.Unavailable.Should().BeEquivalentTo([ValidationResultsErrors.TimedOut, ValidationResultsErrors.Busy]);
        ValidationResultsErrors.Conflicts.Should().NotIntersectWith(ValidationResultsErrors.Unavailable);
        ValidationResultsErrors.Conflicts.Should().NotContain([ValidationResultsErrors.InvalidReason, ValidationResultsErrors.InvalidRevision, ValidationResultsErrors.Corrupt]);
    }

    #endregion

    #region Settings

    [Fact]
    public void Settings_Should_DefaultToOneComputationAndBoundTheNewValues_When_Read()
    {
        var defaults = new Ariva.Infra.Settings.ValidationResultsSettings();

        defaults.Problems().Should().BeEmpty();
        (defaults.MaxConcurrentComputations, defaults.RequestTimeout, defaults.FreezeInterval, defaults.FreezeRetryAfter)
            .Should().Be((1, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)));
        new Ariva.Infra.Settings.ValidationResultsSettings { MaxConcurrentComputations = 5 }.Problems().Should().ContainSingle();
        new Ariva.Infra.Settings.ValidationResultsSettings { RequestTimeout = TimeSpan.FromSeconds(4) }.Problems().Should().ContainSingle();
        new Ariva.Infra.Settings.ValidationResultsSettings { FreezeInterval = TimeSpan.FromHours(2) }.Problems().Should().ContainSingle();
        new Ariva.Infra.Settings.ValidationResultsSettings { FreezeRetryAfter = TimeSpan.FromSeconds(10) }.Problems().Should().ContainSingle();
    }

    #endregion
}
