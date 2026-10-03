using System.Globalization;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Topology;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Topology;

/// <summary>
/// ARV-017 against PostgreSQL with script 0012: a draft is created, edited, validated and published as version 1 with
/// ZoneProfilePublished in the outbox; the next draft copies it, version 2 retires version 1; published geometry cannot
/// be changed through the service (409) nor by SQL (trigger); a polygon of 200 points is stored whole; another site's
/// caller sees nothing; one draft per site.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ZoneProfileWorkflowTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcZoneProfiles Profiles(IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();

    /// <summary>Publishes what the validation shows, as a publisher who reviewed it would.</summary>
    private async Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.ZoneProfileSummaryViewModel>> PublishReviewedAsync(Guid admin, Guid id)
    {
        var reviewed = await _host.AsCallerAsync(admin, s => Profiles(s).ValidateAsync(id, Ct));
        return await _host.AsCallerAsync(admin, s => Profiles(s).PublishAsync(id, new PublishZoneProfileRequest(reviewed.Data?.GeometryHash ?? new string('0', 64)), Ct));
    }

    private static string Rect(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"{x} {y},{x + w} {y},{x + w} {y + h},{x} {y + h}");

    private async Task<(Guid Admin, Guid Level)> SiteAsync(string site, string iata)
    {
        var admin = await _host.CreateUserAsync($"it.zp.{site.ToLowerInvariant()}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest(iata, null, "Zones", "Asia/Dubai"), Ct));
        var terminal = await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "T1", site), Ct));
        var level = await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Arrivals", 0, 100, 50), Ct));
        level.HasErrors.Should().BeFalse(string.Join(", ", level.ErrorMessages ?? []));
        return (admin, level.Data.Id);
    }

    [Fact]
    public async Task Workflow_Should_PublishVersionsWithEventsAndKeepPublishedGeometryFixed_When_RunEndToEnd()
    {
        var (admin, level) = await SiteAsync("ZPA", "ZPA");

        var draft = await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPA", "Arrivals immigration"), Ct));
        draft.HasErrors.Should().BeFalse(string.Join(", ", draft.ErrorMessages ?? []));
        var id = draft.Data.Profile.Id;
        (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPA", "Again"), Ct)))
            .ErrorMessages.Should().Equal(ZoneProfileErrors.DraftExists);

        var queue = await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(id, new AddZoneRequest("Snake A", "Queue", level, Rect(10, 10, 24, 12), LaneCategory: "CIT"), Ct));
        queue.HasErrors.Should().BeFalse(string.Join(", ", queue.ErrorMessages ?? []));
        queue.Data.LaneCategory.Should().Be("CIT", "the queue of the citizens' lane (ARV-057)");
        var notYet = await PublishReviewedAsync(admin, id);
        notYet.ErrorMessages.First().Should().Be(ZoneProfileErrors.NotPublishable);
        notYet.ErrorMessages.Should().HaveCountGreaterThan(1, "the problems follow");

        (await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(id, new AddLineRequest("Entry A", "Entry", level, 10, 12, 10, 16, queue.Data.Id), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(id, new AddLineRequest("Exit A", "Exit", level, 30, 22, 34, 22, queue.Data.Id), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(id, new AddZoneRequest("Service A", "Service", level, Rect(34, 10, 10, 12), queue.Data.Id), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Profiles(s).ValidateAsync(id, Ct))).Data.Publishable.Should().BeTrue();

        var v1 = await PublishReviewedAsync(admin, id);
        v1.HasErrors.Should().BeFalse(string.Join(", ", v1.ErrorMessages ?? []));
        v1.Data.Version.Should().Be(1);
        v1.Data.Status.Should().Be("Published");
        v1.Data.GeometryHash.Should().MatchRegex("^[0-9a-f]{64}$");
        (await _host.ReadAsync<string>("SELECT message_key FROM outbox_message WHERE payload->>'profileId' = @id::text", id)).Should().Be("ZPA");
        (await _host.ReadAsync<string>("SELECT topic FROM outbox_message WHERE payload->>'profileId' = @id::text", id)).Should().Be("ariva.topology.zone-profile-activated.v1");

        (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(id, new AddZoneRequest("Late", "Queue", level, Rect(50, 10, 5, 5)), Ct)))
            .ErrorMessages.Should().Equal(ZoneProfileErrors.NotADraft);
        var sqlEdit = () => _host.ReadAsync<int>("UPDATE zone SET polygon = '0 0,1 0,1 1' WHERE profile_id = @id RETURNING 1", id);
        (await sqlEdit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001", "the trigger keeps published geometry fixed");
        var laneEdit = () => _host.ReadAsync<int>("UPDATE zone SET lane_category = 'VIS' WHERE profile_id = @id RETURNING 1", id);
        (await laneEdit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001", "a published version's lanes are fixed too");

        var next = await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPA", null), Ct));
        next.Data.Profile.BasedOnVersion.Should().Be(1);
        next.Data.Zones.Should().HaveCount(2);
        next.Data.Lines.Should().HaveCount(2);
        var snake = next.Data.Zones.Single(z => z.Name == "Snake A");
        snake.LaneCategory.Should().Be("CIT", "a draft keeps the lanes");
        (await _host.AsCallerAsync(admin, s => Profiles(s).UpdateZoneAsync(next.Data.Profile.Id, snake.Id, new UpdateZoneRequest("Snake A", "10 10,34 10,36 16,34 22,10 22", "VIS"), Ct)))
            .Data.LaneCategory.Should().Be("VIS");
        (await _host.AsCallerAsync(admin, s => Profiles(s).UpdateZoneAsync(next.Data.Profile.Id, snake.Id, new UpdateZoneRequest("Snake A", "10 10,34 10,36 16,34 22,10 22"), Ct)))
            .Data.LaneCategory.Should().Be("VIS", "a request without a lane keeps the zone's lane");
        (await _host.AsCallerAsync(admin, s => Profiles(s).UpdateZoneAsync(next.Data.Profile.Id, snake.Id, new UpdateZoneRequest("Snake A", "10 10,34 10,36 16,34 22,10 22", ""), Ct)))
            .Data.LaneCategory.Should().BeNull("an empty lane clears it");
        (await _host.AsCallerAsync(admin, s => Profiles(s).UpdateZoneAsync(next.Data.Profile.Id, snake.Id, new UpdateZoneRequest("Snake A", "10 10,34 10,36 16,34 22,10 22", "VIS"), Ct)))
            .Data.LaneCategory.Should().Be("VIS");
        var service = next.Data.Zones.Single(z => z.Name == "Service A");
        (await _host.AsCallerAsync(admin, s => Profiles(s).UpdateZoneAsync(next.Data.Profile.Id, service.Id, new UpdateZoneRequest("Service A", service.Polygon, "VIS"), Ct)))
            .HasErrors.Should().BeTrue("only a queue zone is a lane's queue");
        (await _host.AsCallerAsync(admin, s => Profiles(s).UpdateZoneAsync(next.Data.Profile.Id, snake.Id, new UpdateZoneRequest("Snake A", "10 10,34 10,36 16,34 22,10 22", "vis!"), Ct)))
            .HasErrors.Should().BeTrue("a lane is 2 to 4 capitals");
        var v2 = await PublishReviewedAsync(admin, next.Data.Profile.Id);
        v2.Data.Version.Should().Be(2);
        v2.Data.GeometryHash.Should().NotBe(v1.Data.GeometryHash);

        var history = await _host.AsCallerAsync(admin, s => Profiles(s).HistoryAsync("ZPA", Ct));
        history.Data.Select(h => (h.Version, h.Status)).Should().Equal((2, "Published"), (1, "Retired"));
        history.Data.Should().OnlyContain(h => h.PublishedBy == "it-admin" && h.PublishedOn != null && h.CreatedOn != null);
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE message_key = 'ZPA'")).Should().Be(2);
        (await _host.ReadAsync<string>("SELECT payload->>'replacesVersion' FROM outbox_message WHERE payload->>'version' = '2' AND message_key = 'ZPA'")).Should().Be("1");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'ZoneProfile.Published' AND target_name = 'ZPA'")).Should().Be(2);
    }

    [Fact]
    public async Task Draft_Should_StoreA200PointPolygonWholeAndStayInItsSite_When_Edited()
    {
        var (admin, level) = await SiteAsync("ZPB", "ZPB");
        var officer = await _host.CreateUserAsync("it.zp.officer", roles: [RoleCodes.BorderShiftSupervisor]);
        var draft = await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPB", "Round hall"), Ct));
        var id = draft.Data.Profile.Id;

        // 200 points on an ellipse: about 2,800 characters, past NHibernate's default string length of 1,000.
        var points = Enumerable.Range(0, 200).Select(i =>
        {
            var a = 2 * Math.PI * i / 200;
            return string.Create(CultureInfo.InvariantCulture, $"{50 + (40.123 * Math.Cos(a)):0.000} {25 + (20.456 * Math.Sin(a)):0.000}");
        });
        var polygon = string.Join(",", points);
        polygon.Length.Should().BeGreaterThan(2000);
        var zone = await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(id, new AddZoneRequest("Hall", "Queue", level, polygon), Ct));
        zone.HasErrors.Should().BeFalse(string.Join(", ", zone.ErrorMessages ?? []));

        (await _host.ReadAsync<int>("SELECT length(polygon) FROM zone WHERE id = @id", zone.Data.Id)).Should().Be(polygon.Length, "[MaxLength] widens the parameter, so nothing is cut");
        (await _host.AsCallerAsync(officer, s => Profiles(s).GetAsync(id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(officer, s => Profiles(s).HistoryAsync("ZPB", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);

        (await _host.AsCallerAsync(admin, s => Profiles(s).DiscardAsync(id, Ct))).HasErrors.Should().BeFalse();
        (await _host.ReadAsync<long>("SELECT count(*) FROM zone_profile WHERE id = @id", id)).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'ZoneProfile.Discarded' AND target_id = @id", id)).Should().Be(1);
    }

    [Fact]
    public async Task Edits_Should_StayInsideTheProfileAndItsSite_When_IdsPointElsewhere()
    {
        var (admin, level) = await SiteAsync("ZPC", "ZPC");
        var (_, foreignLevel) = await SiteAsync("ZPD", "ZPD");
        var first = (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPC", "C"), Ct))).Data.Profile.Id;
        var other = (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPD", "D"), Ct))).Data.Profile.Id;
        var foreignZone = (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(other, new AddZoneRequest("Snake D", "Queue", foreignLevel, Rect(10, 10, 24, 12)), Ct))).Data.Id;

        (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(first, new AddZoneRequest("Snake", "Queue", foreignLevel, Rect(10, 10, 24, 12)), Ct)))
            .ErrorMessages.Should().Equal(new[] { TopologyErrors.NotFound }, "a level of another site");
        (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(first, new AddZoneRequest("Service", "Service", level, Rect(34, 10, 10, 12), foreignZone), Ct)))
            .ErrorMessages.Should().Equal(new[] { TopologyErrors.NotFound }, "a queue zone of another profile");
        (await _host.AsCallerAsync(admin, s => Profiles(s).UpdateZoneAsync(first, foreignZone, new UpdateZoneRequest("Snake D", Rect(10, 10, 20, 10)), Ct)))
            .ErrorMessages.Should().Equal(new[] { TopologyErrors.NotFound }, "a zone of another profile");
        (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(first, new AddZoneRequest("Desk zone", "Staff", level, Rect(60, 10, 5, 5), DeskId: Guid.CreateVersion7()), Ct)))
            .HasErrors.Should().BeTrue("an unknown desk");
    }

    [Fact]
    public async Task Publish_Should_RefuseTheDraft_When_ItChangedSinceItWasReviewed()
    {
        var (admin, level) = await SiteAsync("ZPE", "ZPE");
        var id = (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPE", "E"), Ct))).Data.Profile.Id;
        var queue = (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(id, new AddZoneRequest("Snake", "Queue", level, Rect(10, 10, 24, 12)), Ct))).Data.Id;
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(id, new AddLineRequest("Entry", "Entry", level, 10, 12, 10, 16, queue), Ct));
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(id, new AddLineRequest("Exit", "Exit", level, 30, 22, 34, 22, queue), Ct));
        var reviewed = await _host.AsCallerAsync(admin, s => Profiles(s).ValidateAsync(id, Ct));
        reviewed.Data.Publishable.Should().BeTrue();

        // Another editor changes the draft after the review.
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(id, new AddLineRequest("Count", "Count", level, 50, 0, 50, 40), Ct));
        var publish = await _host.AsCallerAsync(admin, s => Profiles(s).PublishAsync(id, new PublishZoneProfileRequest(reviewed.Data.GeometryHash), Ct));

        publish.ErrorMessages.Should().Equal(ZoneProfileErrors.ChangedSinceReview);
        (await _host.ReadAsync<string>("SELECT status FROM zone_profile WHERE id = @id", id)).Should().Be("Draft");
        (await PublishReviewedAsync(admin, id)).Data.Version.Should().Be(1, "publishing the geometry as it is now succeeds");
    }

    [Fact]
    public async Task Database_Should_RefuseAZoneOfAnotherProfileAndALaterRetirementDate_When_WrittenDirectly()
    {
        var (admin, level) = await SiteAsync("ZPF", "ZPF");
        var a = (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPF", "F"), Ct))).Data.Profile.Id;
        var zone = (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(a, new AddZoneRequest("Snake", "Queue", level, Rect(10, 10, 24, 12)), Ct))).Data.Id;
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(a, new AddLineRequest("Entry", "Entry", level, 10, 12, 10, 16, zone), Ct));
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(a, new AddLineRequest("Exit", "Exit", level, 30, 22, 34, 22, zone), Ct));
        (await PublishReviewedAsync(admin, a)).HasErrors.Should().BeFalse();
        var b = (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPF", "F2"), Ct))).Data.Profile.Id;
        (await PublishReviewedAsync(admin, b)).HasErrors.Should().BeFalse();

        await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("ZPF", "F3"), Ct));
        var crossProfile = () => _host.ReadAsync<int>(
            "UPDATE zone SET queue_zone_id = (SELECT id FROM zone WHERE profile_id = @id LIMIT 1) WHERE profile_id = (SELECT id FROM zone_profile WHERE site_code = 'ZPF' AND status = 'Draft') RETURNING 1", a);
        var redate = () => _host.ReadAsync<int>("UPDATE zone_profile SET retired_on = now() WHERE id = @id RETURNING 1", a);

        (await crossProfile.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503", "a zone hangs off a queue zone of its own profile");
        (await redate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001", "a retirement date is set once");
    }
}

