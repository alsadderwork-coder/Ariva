using System.Text;
using Ariva.Core;
using Ariva.Core.Advertising;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SkiaSharp;

namespace Ariva.IntegrationTests.Displays;

/// <summary>
/// ARV-143a against PostgreSQL with script 0060: a playlist keeps items of its own site (an advertisement of the library
/// that is not retired, an active slide), a video's slot being its stored length; a refused playlist or schedule stores
/// nothing; a schedule plays an active playlist of its site on queue zones of the published profile or live displays;
/// another site's playlist or schedule answers like one that does not exist (IDOR); the timeline resolves a display's week
/// in the site's time zone; the cap of 100 playlists holds when two creations race for the last place; and the database
/// refuses the runtime role writes that break the same rules (items of another site, a schedule of another site's
/// playlist, a delete). Its own database, so the advertising policy of DMO here reaches no other test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdvertisingPlansServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static Guid? _admin;

    /// <summary>The host's clock starts on Thursday 2026-10-01 08:00 UTC: noon at DMO (Asia/Dubai, UTC+4).</summary>
    private static readonly DateOnly First = new(2026, 9, 30);

    private static readonly DateOnly Last = new(2026, 10, 31);

    /// <summary>The hosts' log entries (each test's host logs here; a test reads the entries of its own display).</summary>
    private static readonly CapturedLogs Logs = new();

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AdvertisingPlans, configure: services => services.AddSingleton<ILoggerProvider>(Logs));

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcAdvertisingPlaylists Playlists(IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingPlaylists>();

    private static ISvcAdvertisingSchedules Schedules(IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingSchedules>();

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Video() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media", "vp9-opus.webm"));

    private async Task<Guid> AdminAsync()
    {
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (_admin is { } existing)
                return existing;
            var admin = await _host.CreateUserAsync("it.plans.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("PLN2", "Advertising plans test site"), Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("PLN3", "Advertising plans cap site"), Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("PLN4", "Advertising schedules cap site"), Ct));
            var reviewed = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().GetAsync("DMO", Ct))).Data.PublishedProfileId;
            var allowed = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().ChangeAsync("DMO",
                new AdvertisingPolicyRequest(true, [], "Airport operator letter AO-IT-143a, commercial team", reviewed), Ct));
            allowed.HasErrors.Should().BeFalse(string.Join(", ", allowed.ErrorMessages ?? []));
            _admin = admin;
            return admin;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private async Task<Guid> ManagerAsync(string userName, params string[] sites)
    {
        var admin = await AdminAsync();
        var user = await _host.CreateUserAsync(userName, roles: [RoleCodes.TerminalDutyManager]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(user, new SiteAccessRequest(false, sites.Length == 0 ? ["DMO"] : sites), Ct));
        return user;
    }

    private async Task<AdvertisingMediaViewModel> MediaAsync(Guid caller, string site = "DMO", byte[] bytes = null, string name = "winter.png", DateOnly? startsOn = null,
        DateOnly? endsOn = null)
    {
        var bytesToSend = bytes ?? Png();
        var result = await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAdvertisingMedia>().UploadAsync(site,
            new AdvertisingMediaUpload(new MemoryStream(bytesToSend), bytesToSend.Length, name, "Gulf Air", "Winter sun", startsOn ?? First, endsOn ?? Last), Ct));
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private async Task<Guid> SlideAsync(Guid caller, string site = "DMO", string title = "Have your passport ready")
    {
        var result = await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcInstructionSlides>().CreateAsync(new InstructionSlideRequest(site, "Site", [],
            "Passport", 8, false, new Dictionary<string, InstructionTextRequest> { ["en"] = new(title, null) }), Ct));
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data.Id;
    }

    private static AdvertisingPlaylistRequest Playlist(string name, params AdvertisingPlaylistItemRequest[] items) => new(name, items);

    private static AdvertisingPlaylistItemRequest Item(string kind, Guid id, int? seconds = 10, int? weight = null) => new(kind, id, seconds, weight);

    private async Task<AdvertisingPlaylistViewModel> CreatedPlaylistAsync(Guid caller, string site, AdvertisingPlaylistRequest request)
    {
        var result = await _host.AsCallerAsync(caller, s => Playlists(s).CreateAsync(site, request, Ct));
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private static AdvertisingScheduleRequest Schedule(Guid playlist, string scope = "Site", string[] targets = null, string[] days = null, string opens = "00:00",
        string closes = "24:00") =>
        new(playlist, scope, targets ?? [], days ?? ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"], opens, closes);

    private async Task<AdvertisingMediaViewModel> ApprovedAsync(Guid uploader, Guid approver, DateOnly? startsOn = null, DateOnly? endsOn = null)
    {
        var item = await MediaAsync(uploader, startsOn: startsOn, endsOn: endsOn);
        var approved = await _host.AsCallerAsync(approver, s => s.GetRequiredService<ISvcAdvertisingMedia>().ApproveAsync("DMO", item.Id,
            new ApproveAdvertisingMediaRequest(item.ReviewToken), Ct));
        approved.HasErrors.Should().BeFalse(string.Join(", ", approved.ErrorMessages ?? []));
        return approved.Data;
    }

    /// <summary>A display of DMO for the board tests: its id and credential prefix, as the player presents them.</summary>
    private async Task<DisplayPlayer> PlayerAsync(Guid admin, string code, string secondRegion = "Advertising", string layout = "Split", int? pause = null,
        int stale = 150)
    {
        var request = new DisplayRequest("DMO", code, "Arrivals hall", null, "Landscape", ["en", "ar"], 5, 1, stale,
            [new DisplayEntryRequest("A-CIT", new Dictionary<string, string> { ["en"] = "Citizens", ["ar"] = "المواطنون" })],
            new Dictionary<string, string> { ["en"] = "Please follow the signs", ["ar"] = "يرجى اتباع اللافتات" }, true, layout, 60, secondRegion, pause);
        var created = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDisplays>().CreateAsync(request, Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        return new DisplayPlayer(created.Data.Display.Id, code, "DMO", created.Data.Display.CredentialPrefix);
    }

    /// <summary>The class shares one database: the other tests' DMO schedules would apply to a board test's displays too.</summary>
    private async Task RetireDmoSchedulesAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var others = new NpgsqlCommand("UPDATE advertising_schedule SET status = 'Retired' WHERE site_code = 'DMO'", connection);
        await others.ExecuteNonQueryAsync(Ct);
    }

    private async Task<DisplayBoardViewModel> BoardAsync(DisplayPlayer player)
    {
        var board = await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcDisplayBoard>().GetAsync(player.Id, player.CredentialPrefix, Ct));
        board.HasErrors.Should().BeFalse(string.Join(", ", board.ErrorMessages ?? []));
        return board.Data;
    }

    private async Task<string> DisplayAsync(Guid admin, string code, string zone)
    {
        var request = new DisplayRequest("DMO", code, "Arrivals hall", null, "Landscape", ["en"], 5, 1, 150,
            [new DisplayEntryRequest(zone, new Dictionary<string, string> { ["en"] = "Citizens" })], new Dictionary<string, string> { ["en"] = "Please follow the signs" },
            true, "Split", 60, "Advertising");
        var created = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDisplays>().CreateAsync(request, Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        return code;
    }

    [Fact]
    public async Task Playlist_Should_KeepItemsOfItsSiteAsAnAuditedRow_When_ItNamesAnImageAVideoAndASlide()
    {
        var manager = await ManagerAsync("it.plans.create");
        var image = await MediaAsync(manager);
        var video = await MediaAsync(manager, bytes: Video(), name: "spot.webm");
        var slide = await SlideAsync(await AdminAsync());

        var created = await CreatedPlaylistAsync(manager, "DMO", Playlist("  Morning bank  ", Item("Media", image.Id, 12, 3), Item("Media", video.Id, 30), Item("Slide", slide, 8)));

        created.Should().Match<AdvertisingPlaylistViewModel>(p => p.Name == "Morning bank" && p.Status == "Active" && p.SiteCode == "DMO");
        created.Items.Select(i => (i.Kind, i.Id, i.Seconds, i.Weight)).Should().Equal(("Media", image.Id, 12, 3), ("Media", video.Id, 3, 1), ("Slide", slide, 8, 1));
        created.Items[0].Should().Match<AdvertisingPlaylistItemViewModel>(i => i.Label == "Gulf Air: Winter sun" && !i.Playable && i.MediaStatus == "Draft",
            "a draft is listed and plays only once approved");
        created.Items[1].ContentType.Should().Be("video/webm", "a video's slot is its own length, rounded up, whatever was asked");
        created.Items[2].Should().Match<AdvertisingPlaylistItemViewModel>(i => i.Label == "Have your passport ready" && i.Playable);
        created.TotalSeconds.Should().Be(23);
        await using (var shorter = new AccountsHost(fixture, new() { ["Advertising:Videos:MaxSeconds"] = "2" }, TestDatabase.AdvertisingPlans))
        {
            await shorter.CreateUserAsync("it.plans.shorter"); // starts the second host on the same database
            var capped = await shorter.AsCallerAsync(manager, s => Playlists(s).CreateAsync("DMO", Playlist("Short spots", Item("Media", video.Id, 30)), Ct));
            capped.Data.Items.Should().ContainSingle().Which.Seconds.Should().Be(2, "an installation that lowered its video length plays a longer video for that long");
        }

        (await _host.ReadAsync<string>("SELECT items FROM advertising_playlist WHERE id = @id", created.Id)).Should().Contain("\"kind\":\"Media\"");
        (await _host.ReadAsync<string>("SELECT actor_id::text || ' ' || after_summary FROM audit_entry WHERE action = 'AdvertisingPlaylist.Created' AND target_id = @id",
            created.Id)).Should().StartWith($"{manager} ").And.Contain($"M:{image.Id:N}:12:3").And.Contain($"S:{slide:N}:8:1");
    }

    [Fact]
    public async Task Playlist_Should_StoreNothing_When_AnItemIsNotTheSitesOwnOrBreaksARule()
    {
        var manager = await ManagerAsync("it.plans.refused", "DMO", "PLN2");
        var admin = await AdminAsync();
        var ours = await MediaAsync(manager);
        var theirs = await MediaAsync(manager, "PLN2");
        var retired = await MediaAsync(manager);
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingMedia>().RetireAsync("DMO", retired.Id, Ct))).HasErrors.Should().BeFalse();
        var oldSlide = await SlideAsync(admin, title: "Old slide");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcInstructionSlides>().RetireAsync(oldSlide, Ct))).HasErrors.Should().BeFalse();
        var before = await _host.ReadAsync<long>("SELECT count(*) FROM advertising_playlist");

        foreach (var (reason, request, error) in new (string, AdvertisingPlaylistRequest, string)[]
                 {
                     ("another site's advertisement", Playlist("Bank", Item("Media", theirs.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("a retired advertisement", Playlist("Bank", Item("Media", retired.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("a retired slide", Playlist("Bank", Item("Slide", oldSlide)), AdvertisingPlanErrors.UnknownItems),
                     ("an id that is no item", Playlist("Bank", Item("Media", Guid.CreateVersion7())), AdvertisingPlanErrors.UnknownItems),
                     ("an advertisement named as a slide", Playlist("Bank", Item("Slide", ours.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("an item twice", Playlist("Bank", Item("Media", ours.Id), Item("Media", ours.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("an image for 3 seconds", Playlist("Bank", Item("Media", ours.Id, 3)), AdvertisingPlanErrors.ImageSeconds),
                     ("an image without seconds", Playlist("Bank", Item("Media", ours.Id, null)), AdvertisingPlanErrors.ImageSeconds),
                     ("a kind in lower case", Playlist("Bank", Item("media", ours.Id)), AdvertisingPlanErrors.UnknownKind),
                     ("a kind that is a number", Playlist("Bank", Item("0", ours.Id)), AdvertisingPlanErrors.UnknownKind),
                     ("no items", Playlist("Bank"), "A playlist has 1 to 40 items.")
                 })
        {
            var result = await _host.AsCallerAsync(manager, s => Playlists(s).CreateAsync("DMO", request, Ct));
            result.ErrorMessages.Should().Contain(error, reason);
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_playlist")).Should().Be(before, "nothing refused is stored");
    }

    [Fact]
    public async Task Schedule_Should_PlayAnActivePlaylistOfItsSiteOnItsZonesAndDisplays_When_Saved()
    {
        var manager = await ManagerAsync("it.plans.schedule", "DMO", "PLN2");
        var admin = await AdminAsync();
        var media = await MediaAsync(manager);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Day", Item("Media", media.Id)));
        var theirs = await CreatedPlaylistAsync(manager, "PLN2", Playlist("Theirs", Item("Media", (await MediaAsync(manager, "PLN2")).Id)));
        var retired = await CreatedPlaylistAsync(manager, "DMO", Playlist("Old", Item("Media", media.Id)));
        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", retired.Id, Ct))).HasErrors.Should().BeFalse();
        await DisplayAsync(admin, "PLN-SCH", "A-CIT");
        var before = await _host.ReadAsync<long>("SELECT count(*) FROM advertising_schedule");

        foreach (var (reason, request, error) in new (string, AdvertisingScheduleRequest, string)[]
                 {
                     ("another site's playlist", Schedule(theirs.Id), AdvertisingPlanErrors.UnknownPlaylist),
                     ("a retired playlist", Schedule(retired.Id), AdvertisingPlanErrors.UnknownPlaylist),
                     ("a zone that is not a queue zone of the published profile", Schedule(playlist.Id, "Zone", ["Nowhere"]), InstructionSlideErrors.UnknownZones),
                     ("a display that does not exist", Schedule(playlist.Id, "Display", ["NO-SUCH"]), InstructionSlideErrors.UnknownDisplays),
                     ("a scope in lower case", Schedule(playlist.Id, "site"), AdvertisingPlanErrors.UnknownScope),
                     ("no day", Schedule(playlist.Id, days: []), "A schedule names 1 to 7 different days: Monday, Tuesday, Wednesday, Thursday, Friday, Saturday or Sunday."),
                     ("a window of no length", Schedule(playlist.Id, opens: "10:00", closes: "10:00"), Core.Availability.WeeklyHours.InvalidInterval)
                 })
        {
            var result = await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", request, Ct));
            result.ErrorMessages.Should().Contain(error, reason);
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_schedule")).Should().Be(before, "nothing refused is stored");

        var created = await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Zone", ["A-CIT"], ["Friday", "Monday"], "22:00", "02:00"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        created.Data.Should().Match<AdvertisingScheduleViewModel>(s => s.PlaylistName == "Day" && s.PlaylistActive && s.Scope == "Zone" && s.Opens == "22:00" &&
                                                                        s.Closes == "02:00" && s.Status == "Active");
        created.Data.Days.Should().Equal("Monday", "Friday");
        var display = await _host.AsCallerAsync(manager, s => Schedules(s).UpdateAsync("DMO", created.Data.Id, Schedule(playlist.Id, "Display", ["PLN-SCH"]), Ct));
        display.Data.Targets.Should().Equal("PLN-SCH");
        (await _host.AsCallerAsync(manager, s => Schedules(s).RetireAsync("DMO", created.Data.Id, Ct))).Data.Status.Should().Be("Retired");
        (await _host.AsCallerAsync(manager, s => Schedules(s).RestoreAsync("DMO", created.Data.Id, Ct))).Data.Status.Should().Be("Active");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action LIKE 'AdvertisingSchedule.%'", created.Data.Id))
            .Should().Be(4, "created, updated, retired and restored");
    }

    [Fact]
    public async Task Lookup_Should_AnswerNotFound_When_ThePlanIsAnotherSitesOrTheCallerCannotSeeTheSite()
    {
        var manager = await ManagerAsync("it.plans.idor", "DMO", "PLN2");
        var outsider = await ManagerAsync("it.plans.outsider", "PLN2");
        var media = await MediaAsync(manager);
        var ours = await CreatedPlaylistAsync(manager, "DMO", Playlist("Ours", Item("Media", media.Id)));
        var theirs = await CreatedPlaylistAsync(manager, "PLN2", Playlist("Theirs", Item("Media", (await MediaAsync(manager, "PLN2")).Id)));
        var schedule = (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(ours.Id), Ct))).Data;

        (await _host.AsCallerAsync(manager, s => Playlists(s).GetAsync("DMO", theirs.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Playlists(s).UpdateAsync("DMO", theirs.Id, Playlist("Taken", Item("Media", media.Id)), Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", theirs.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Schedules(s).GetAsync("PLN2", schedule.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(outsider, s => Playlists(s).ListAsync("DMO", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(outsider, s => Schedules(s).RetireAsync("DMO", schedule.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(outsider, s => Schedules(s).TimelineAsync("DMO", "PLN-SCH", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.ReadAsync<string>("SELECT name FROM advertising_playlist WHERE id = @id", theirs.Id)).Should().Be("Theirs");
        (await _host.ReadAsync<string>("SELECT status FROM advertising_schedule WHERE id = @id", schedule.Id)).Should().Be("Active");
    }

    [Fact]
    public async Task Timeline_Should_ResolveTheDisplaysWeekInTheSitesTimeZone_When_SchedulesOverlap()
    {
        var manager = await ManagerAsync("it.plans.timeline");
        var admin = await AdminAsync();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            // The class shares one database: the other tests' DMO schedules would apply to this display too.
            await connection.OpenAsync(Ct);
            await using var others = new NpgsqlCommand("UPDATE advertising_schedule SET status = 'Retired' WHERE site_code = 'DMO'", connection);
            await others.ExecuteNonQueryAsync(Ct);
        }

        var media = await MediaAsync(manager);
        var allDay = await CreatedPlaylistAsync(manager, "DMO", Playlist("All day", Item("Media", media.Id)));
        var peak = await CreatedPlaylistAsync(manager, "DMO", Playlist("Peak bank", Item("Media", media.Id)));
        await DisplayAsync(admin, "PLN-TML", "A-CIT");
        await DisplayAsync(admin, "PLN-OTH", "A-VIS");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(allDay.Id), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(peak.Id, "Zone", ["A-CIT"], ["Thursday"], "12:00", "14:00"), Ct)))
            .HasErrors.Should().BeFalse();

        var timeline = await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "PLN-TML", Ct));

        timeline.HasErrors.Should().BeFalse(string.Join(", ", timeline.ErrorMessages ?? []));
        timeline.Data.TimeZone.Should().Be("Asia/Dubai");
        timeline.Data.SecondRegion.Should().Be("Advertising");
        timeline.Data.FromUtc.Should().Be(new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc), "midnight of Thursday 1 October at DMO");
        timeline.Data.ToUtc.Should().Be(timeline.Data.FromUtc.AddDays(7));
        var segments = timeline.Data.Segments.Select(s => (s.FromLocal, s.ToLocal, s.PlaylistName)).ToList();
        segments.Should().Equal(
            ("2026-10-01T00:00", "2026-10-01T12:00", "All day"),
            ("2026-10-01T12:00", "2026-10-01T14:00", "Peak bank"),
            ("2026-10-01T14:00", "2026-10-08T00:00", "All day"));
        timeline.Data.Segments[1].FromUtc.Should().Be(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        (await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "PLN-OTH", Ct))).Data.Segments.Should().ContainSingle()
            .Which.PlaylistName.Should().Be("All day", "the peak bank is for the citizens' hall only");

        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", allDay.Id, Ct))).HasErrors.Should().BeFalse();
        var afterRetire = await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "PLN-TML", Ct));
        afterRetire.Data.Segments.Select(s => s.PlaylistName).Should().Equal([null, "Peak bank", null], "a retired playlist's schedule stops applying: instructions");
        (await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "NO-SUCH", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", null, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
    }

    [Fact]
    public async Task Create_Should_KeepThePlaylistCap_When_TwoCreationsRaceForTheLastPlace()
    {
        var manager = await ManagerAsync("it.plans.cap", "PLN3");
        var media = await MediaAsync(manager, "PLN3");
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            await using var fill = new NpgsqlCommand(
                "INSERT INTO advertising_playlist (id, site_code, name, items, status) SELECT gen_random_uuid(), 'PLN3', 'Filler ' || n, " +
                "'[{\"kind\":\"Media\",\"id\":\"' || @media::text || '\",\"seconds\":10,\"weight\":1}]', 'Active' FROM generate_series(1, @count) n", connection);
            fill.Parameters.AddWithValue("media", media.Id);
            fill.Parameters.AddWithValue("count", AdvertisingPlaylist.MaxPlaylistsPerSite - 1);
            (await fill.ExecuteNonQueryAsync(Ct)).Should().Be(AdvertisingPlaylist.MaxPlaylistsPerSite - 1);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(n =>
            _host.AsCallerAsync(manager, s => Playlists(s).CreateAsync("PLN3", Playlist($"Last {n}", Item("Media", media.Id)), Ct))));

        results.Count(r => !r.HasErrors).Should().Be(1, "one takes the last place");
        results.Single(r => r.HasErrors).ErrorMessages.Should().Equal(AdvertisingPlanErrors.TooManyPlaylists);
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_playlist WHERE site_code = 'PLN3'")).Should().Be(AdvertisingPlaylist.MaxPlaylistsPerSite);
    }

    [Fact]
    public async Task Create_Should_KeepTheScheduleCap_When_TwoCreationsRaceForTheLastPlace()
    {
        var manager = await ManagerAsync("it.plans.schedcap", "PLN4");
        var playlist = await CreatedPlaylistAsync(manager, "PLN4", Playlist("Capped", Item("Media", (await MediaAsync(manager, "PLN4")).Id)));
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            // Retired ones count: the cap is on rows, so retiring and creating again cannot pass it.
            await using var fill = new NpgsqlCommand(
                "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                "SELECT gen_random_uuid(), 'PLN4', @playlist, 'Site', '[]', 'Monday', 0, 60, CASE WHEN n % 2 = 0 THEN 'Retired' ELSE 'Active' END " +
                "FROM generate_series(1, @count) n", connection);
            fill.Parameters.AddWithValue("playlist", playlist.Id);
            fill.Parameters.AddWithValue("count", AdvertisingSchedule.MaxSchedulesPerSite - 1);
            (await fill.ExecuteNonQueryAsync(Ct)).Should().Be(AdvertisingSchedule.MaxSchedulesPerSite - 1);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("PLN4", Schedule(playlist.Id), Ct))));

        results.Count(r => !r.HasErrors).Should().Be(1, "one takes the last place");
        results.Single(r => r.HasErrors).ErrorMessages.Should().Equal(AdvertisingPlanErrors.TooManySchedules);
        AdvertisingPlanErrors.Conflicts.Should().Contain(AdvertisingPlanErrors.TooManySchedules, "the API answers it with 409");
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_schedule WHERE site_code = 'PLN4'")).Should().Be(AdvertisingSchedule.MaxSchedulesPerSite);
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("PLN4", Schedule(playlist.Id), Ct))).ErrorMessages
            .Should().Equal(AdvertisingPlanErrors.TooManySchedules);
    }

    [Fact]
    public async Task Board_Should_CarryThePlayablePlaylistThatAppliesNow_When_TheSitesPolicyAllowsAdvertising()
    {
        var admin = await AdminAsync();
        await RetireDmoSchedulesAsync();
        var manager = await ManagerAsync("it.plans.board");
        var approver = await ManagerAsync("it.plans.board.second");
        var first = await ApprovedAsync(manager, approver);
        var second = await ApprovedAsync(manager, approver);
        var draft = await MediaAsync(manager);
        var slide = await SlideAsync(admin, title: "Boarding passes ready");
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Board bank", Item("Media", first.Id, 12, 2), Item("Media", draft.Id), Item("Slide", slide, 8),
            Item("Media", second.Id, 6)));
        var player = await PlayerAsync(admin, "PLN-BRD", pause: 20);
        var instructions = await PlayerAsync(admin, "PLN-INS", secondRegion: "Instructions");
        var full = await PlayerAsync(admin, "PLN-FUL", layout: "Full");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Display", ["PLN-BRD", "PLN-INS", "PLN-FUL"]), Ct)))
            .HasErrors.Should().BeFalse();

        var board = await BoardAsync(player);

        board.SecondRegion.Should().Be("Advertising");
        board.Advertising.Should().NotBeNull();
        board.Advertising.PlaylistId.Should().Be(playlist.Id);
        board.Advertising.UntilUtc.Should().Be(_host.Clock.GetUtcNow().UtcDateTime.AddSeconds(150),
            "no further than the display's board may be shown (its stale threshold), the plan holding till then");
        board.Advertising.PauseMinutes.Should().Be(20);
        board.Advertising.Items.Select(i => (i.Kind, i.MediaId, i.ContentType, i.Seconds)).Should().Equal(
            ("Media", first.Id, "image/png", 12), ("Slide", null, null, 8), ("Media", second.Id, "image/png", 6));
        board.Advertising.Items[1].Slide.Should().Match<DisplayBoardSlideViewModel>(s => s.DurationSeconds == 8 && s.Texts.Count == 1 &&
            s.Texts[0].Language == "en" && s.Texts[0].Title == "Boarding passes ready", "a slide in its playlist's slot and the display's languages");
        board.Advertising.Order.Should().Equal([0, 1, 2, 0], "weights of 2, 1 and 1; the draft never plays");
        (await BoardAsync(instructions)).Advertising.Should().BeNull("a display set to instructions");
        (await BoardAsync(full)).Advertising.Should().BeNull("a full board has no second region");

        // An advertisement retired since leaves the playlist and plays no more; a retired playlist stops the plan.
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingMedia>().RetireAsync("DMO", second.Id, Ct))).HasErrors.Should().BeFalse();
        (await BoardAsync(player)).Advertising.Order.Should().Equal([0, 1, 0]);
        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", playlist.Id, Ct))).HasErrors.Should().BeFalse();
        (await BoardAsync(player)).Advertising.Should().BeNull("instructions while the playlist is retired");
        (await _host.AsCallerAsync(manager, s => Playlists(s).RestoreAsync("DMO", playlist.Id, Ct))).HasErrors.Should().BeFalse();

        // The policy is read on every board: turned off, the board carries instructions though the plan is kept.
        var policies = (Func<IServiceProvider, ISvcAdvertisingPolicies>)(s => s.GetRequiredService<ISvcAdvertisingPolicies>());
        var reviewed = (await _host.AsCallerAsync(admin, s => policies(s).GetAsync("DMO", Ct))).Data.PublishedProfileId;
        try
        {
            (await _host.AsCallerAsync(admin, s => policies(s).ChangeAsync("DMO",
                new AdvertisingPolicyRequest(false, [], "Airport operator letter AO-IT-143b, paused", reviewed), Ct))).HasErrors.Should().BeFalse();
            var off = await BoardAsync(player);
            off.SecondRegion.Should().Be("Instructions");
            off.Advertising.Should().BeNull("a plan is never itself permission to advertise");
        }
        finally
        {
            (await _host.AsCallerAsync(admin, s => policies(s).ChangeAsync("DMO",
                new AdvertisingPolicyRequest(true, [], "Airport operator letter AO-IT-143a, commercial team", reviewed), Ct))).HasErrors.Should().BeFalse();
        }

        (await BoardAsync(player)).Advertising.Order.Should().Equal([0, 1, 0], "allowed again, the plan plays again");
    }

    [Fact]
    public async Task Board_Should_EndThePlanWhenItsWindowCloses_When_AScheduleCoversPartOfTheDay()
    {
        var admin = await AdminAsync();
        await RetireDmoSchedulesAsync();
        var manager = await ManagerAsync("it.plans.board.window");
        var approver = await ManagerAsync("it.plans.board.window.second");
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Lunch bank", Item("Media", (await ApprovedAsync(manager, approver)).Id, 10)));
        var player = await PlayerAsync(admin, "PLN-WIN", stale: 1800);
        var draftOnly = await CreatedPlaylistAsync(manager, "DMO", Playlist("Drafts", Item("Media", (await MediaAsync(manager)).Id, 10)));
        var drafts = await PlayerAsync(admin, "PLN-DRF");
        // Thursday at DMO: 11:00 to 12:10 local is 07:00 to 08:10 UTC, and the host's clock says 08:00 UTC.
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Display", ["PLN-WIN"], ["Thursday"], "11:00", "12:10"), Ct)))
            .HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(draftOnly.Id, "Display", ["PLN-DRF"]), Ct))).HasErrors.Should().BeFalse();

        (await BoardAsync(player)).Advertising.UntilUtc.Should().Be(new DateTime(2026, 10, 1, 8, 10, 0, DateTimeKind.Utc),
            "the window closes at 12:10 local, within the display's 30 minutes ahead");
        (await BoardAsync(drafts)).Advertising.Should().BeNull("a playlist with nothing that may play today: instructions");

        // Script 0061 holds the wait rule for every writer: 1 to 240 minutes, or none.
        foreach (var minutes in new[] { 0, 241 })
        {
            await using var runtime = await RuntimeAsync();
            await using var command = new NpgsqlCommand("UPDATE display SET advertising_pause_minutes = @minutes WHERE id = @id", runtime.Connection, runtime.Transaction);
            command.Parameters.AddWithValue("minutes", minutes);
            command.Parameters.AddWithValue("id", player.Id);
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>($"{minutes} minutes")).Which.Should().Match<PostgresException>(e =>
                e.SqlState == "23514" && e.ConstraintName == "ck_display_advertising_pause");
        }
    }

    [Fact]
    public async Task Board_Should_PlayAdvertisementsWithinTheirDatesOnTheSitesLocalDay_When_ItIsNotTheUtcDay()
    {
        await RetireDmoSchedulesAsync();
        var admin = await AdminAsync();
        var manager = await ManagerAsync("it.plans.board.dates");
        var approver = await ManagerAsync("it.plans.board.dates.second");
        // Thursday 1 October at DMO: one advertisement ends that day, one starts the next, one runs throughout.
        var endsToday = await ApprovedAsync(manager, approver, First, new DateOnly(2026, 10, 1));
        var startsTomorrow = await ApprovedAsync(manager, approver, new DateOnly(2026, 10, 2), Last);
        var throughout = await ApprovedAsync(manager, approver);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Dated bank", Item("Media", endsToday.Id), Item("Media", startsTomorrow.Id),
            Item("Media", throughout.Id)));
        var player = await PlayerAsync(admin, "PLN-DAY");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Display", ["PLN-DAY"]), Ct))).HasErrors.Should().BeFalse();

        (await BoardAsync(player)).Advertising.Items.Select(i => i.MediaId).Should().Equal([endsToday.Id, throughout.Id], "12:00 on 1 October at DMO");

        // 21:00 UTC on 1 October is 01:00 on 2 October at DMO (UTC+4): the site's day has turned, the UTC day has not.
        _host.Clock.Advance(TimeSpan.FromHours(13));
        try
        {
            var night = await BoardAsync(player);
            night.Advertising.Items.Select(i => i.MediaId).Should().Equal([startsTomorrow.Id, throughout.Id], "the site's local day, not the UTC day");
            night.Advertising.Order.Should().Equal([0, 1]);
        }
        finally
        {
            _host.Clock.Advance(TimeSpan.FromHours(-13));
        }
    }

    private static ISvcAdvertisingPlays Plays(IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingPlays>();

    private DateTime Now => _host.Clock.GetUtcNow().UtcDateTime;

    private async Task<Result<AdvertisingPlaysViewModel>> RecordAsync(DisplayPlayer player, params AdvertisingPlayRequest[] plays) =>
        await _host.AsCallerAsync(null, s => Plays(s).RecordAsync(player.Id, player.CredentialPrefix, new AdvertisingPlaysRequest(plays), Ct));

    [Fact]
    public async Task Record_Should_TakeEachScheduledPlayOnceAndRefuseTheRest_When_ADisplayReportsABatch()
    {
        var admin = await AdminAsync();
        var manager = await ManagerAsync("it.plans.plays", "DMO", "PLN2");
        var approver = await ManagerAsync("it.plans.plays.second", "DMO", "PLN2");
        var shown = await ApprovedAsync(manager, approver);
        var elsewhere = await ApprovedAsync(manager, approver);
        var theirs = (await MediaAsync(manager, "PLN2")).Id;
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Played bank", Item("Media", shown.Id)));
        var other = await CreatedPlaylistAsync(manager, "DMO", Playlist("Other bank", Item("Media", elsewhere.Id)));
        var player = await PlayerAsync(admin, "PLN-PLY");
        var second = await PlayerAsync(admin, "PLN-PL2");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Display", ["PLN-PLY"]), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(other.Id, "Display", ["PLN-PL2"]), Ct))).HasErrors.Should().BeFalse();
        var started = Now.AddMinutes(-5);

        var batch = await RecordAsync(player,
            new AdvertisingPlayRequest(shown.Id, started, 10_000, true),
            new AdvertisingPlayRequest(shown.Id, started, 10_000, true),
            new AdvertisingPlayRequest(shown.Id, started.AddSeconds(10), 4_000, false),
            new AdvertisingPlayRequest(elsewhere.Id, started, 10_000, true),
            new AdvertisingPlayRequest(theirs, started, 10_000, true),
            new AdvertisingPlayRequest(shown.Id, Now.AddMinutes(5), 1_000, true),
            new AdvertisingPlayRequest(shown.Id, Now.AddDays(-2), 10_000, true),
            new AdvertisingPlayRequest(shown.Id, DateTime.SpecifyKind(started, DateTimeKind.Unspecified), 10_000, true));

        batch.HasErrors.Should().BeFalse(string.Join(", ", batch.ErrorMessages ?? []));
        batch.Data.Should().Be(new AdvertisingPlaysViewModel(2, 1, 5),
            "two plays taken (the same play twice counts once); another display's advertisement, another site's, a future, an old and a local time refused");
        (await RecordAsync(player, new AdvertisingPlayRequest(shown.Id, started, 10_000, true))).Data.Should().Be(new AdvertisingPlaysViewModel(0, 1, 0),
            "a batch sent again counts once");
        (await RecordAsync(second, new AdvertisingPlayRequest(elsewhere.Id, started, 10_000, true))).Data.Accepted.Should().Be(1, "the other display's own advertisement");
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_play WHERE display_id = @id", player.Id)).Should().Be(2);
        (await _host.ReadAsync<string>("SELECT string_agg(playlist_id::text || ':' || duration_ms || ':' || completed, ',' ORDER BY started_utc) FROM advertising_play WHERE display_id = @id",
            player.Id)).Should().Be($"{playlist.Id}:10000:true,{playlist.Id}:4000:false");

        (await _host.AsCallerAsync(null, s => Plays(s).RecordAsync(player.Id, "WRONGPREFIX12", new AdvertisingPlaysRequest([new(shown.Id, started, 1, true)]), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(null, s => Plays(s).RecordAsync(player.Id, player.CredentialPrefix, new AdvertisingPlaysRequest([]), Ct)))
            .ErrorMessages.Should().Equal(AdvertisingPlays.TooMany);
        var oversized = Enumerable.Range(0, AdvertisingPlays.MaxBatch + 1).Select(i => new AdvertisingPlayRequest(shown.Id, started.AddSeconds(-i), 1, true)).ToList();
        (await _host.AsCallerAsync(null, s => Plays(s).RecordAsync(player.Id, player.CredentialPrefix, new AdvertisingPlaysRequest(oversized), Ct)))
            .ErrorMessages.Should().Equal(AdvertisingPlays.TooMany);
    }

    [Fact]
    public async Task Record_Should_RefusePlaysTheDisplayCouldNotHaveShown_When_TheyBreakTheirSlotOverlapOrThePolicy()
    {
        var admin = await AdminAsync();
        var manager = await ManagerAsync("it.plans.plays.rules");
        var approver = await ManagerAsync("it.plans.plays.rules.second");
        var shown = await ApprovedAsync(manager, approver);
        var tomorrow = await ApprovedAsync(manager, approver, new DateOnly(2026, 10, 2), Last);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Rules bank", Item("Media", shown.Id, 10), Item("Media", tomorrow.Id, 10)));
        var player = await PlayerAsync(admin, "PLN-RUL");
        var instructions = await PlayerAsync(admin, "PLN-RIN", secondRegion: "Instructions");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Display", ["PLN-RUL", "PLN-RIN"]), Ct))).HasErrors.Should().BeFalse();
        var started = Now.AddMinutes(-20);

        var batch = await RecordAsync(player,
            new AdvertisingPlayRequest(shown.Id, started, 10_000, true),
            new AdvertisingPlayRequest(shown.Id, started.AddSeconds(5), 4_000, false),
            new AdvertisingPlayRequest(shown.Id, started.AddSeconds(30), 15_000, false),
            new AdvertisingPlayRequest(shown.Id, started.AddSeconds(60), 5_000, true),
            new AdvertisingPlayRequest(tomorrow.Id, started.AddSeconds(90), 10_000, true),
            new AdvertisingPlayRequest(shown.Id, started.AddSeconds(120), 9_500, true));

        batch.Data.Should().Be(new AdvertisingPlaysViewModel(2, 0, 4),
            "taken: a play and one within its slot's tolerance; refused: one on screen with the first, one longer than its slot, one completed in half its slot, one before its dates");

        // Against what is stored: a later batch claiming screen time already played is refused; one that only touches it is taken.
        (await RecordAsync(player, new AdvertisingPlayRequest(shown.Id, started.AddSeconds(3), 10_000, true), new AdvertisingPlayRequest(shown.Id, started.AddSeconds(10), 10_000, true)))
            .Data.Should().Be(new AdvertisingPlaysViewModel(1, 0, 1));

        // As the board: a display set to instructions, or a site whose policy is off, played no advertising (ARV-145).
        (await RecordAsync(instructions, new AdvertisingPlayRequest(shown.Id, started, 10_000, true))).Data.Should().Be(new AdvertisingPlaysViewModel(0, 0, 1),
            "a display set to instructions, though a schedule names it");
        var policies = (Func<IServiceProvider, ISvcAdvertisingPolicies>)(s => s.GetRequiredService<ISvcAdvertisingPolicies>());
        var reviewed = (await _host.AsCallerAsync(admin, s => policies(s).GetAsync("DMO", Ct))).Data.PublishedProfileId;
        var late = new AdvertisingPlayRequest(shown.Id, started.AddSeconds(200), 10_000, true);
        try
        {
            (await _host.AsCallerAsync(admin, s => policies(s).ChangeAsync("DMO",
                new AdvertisingPolicyRequest(false, [], "Airport operator letter AO-IT-144, paused", reviewed), Ct))).HasErrors.Should().BeFalse();
            (await RecordAsync(player, late)).Data.Should().Be(new AdvertisingPlaysViewModel(0, 0, 1), "a plan is never itself permission to advertise");
        }
        finally
        {
            (await _host.AsCallerAsync(admin, s => policies(s).ChangeAsync("DMO",
                new AdvertisingPolicyRequest(true, [], "Airport operator letter AO-IT-143a, commercial team", reviewed), Ct))).HasErrors.Should().BeFalse();
        }

        (await RecordAsync(player, late)).Data.Should().Be(new AdvertisingPlaysViewModel(1, 0, 0), "allowed again");
        (await _host.ReadAsync<string>("""
            SELECT string_agg(extract(epoch FROM p.started_utc - f.first)::int || ':' || p.duration_ms, ',' ORDER BY p.started_utc)
              FROM advertising_play p, (SELECT min(started_utc) AS first FROM advertising_play WHERE display_id = @id) f
             WHERE p.display_id = @id
            """, player.Id)).Should().Be("0:10000,10:10000,120:9500,200:10000", "seconds after the first play, and how long each lasted");
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_play WHERE display_id = @id", instructions.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Record_Should_TakeOnePlayOfTheAirtime_When_PlaysAreShortNestedOrSentAtOnce()
    {
        var admin = await AdminAsync();
        var manager = await ManagerAsync("it.plans.plays.stack");
        var approver = await ManagerAsync("it.plans.plays.stack.second");
        var shown = await ApprovedAsync(manager, approver);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Stacked bank", Item("Media", shown.Id, 10)));
        var player = await PlayerAsync(admin, "PLN-STK");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Display", ["PLN-STK"]), Ct))).HasErrors.Should().BeFalse();
        var started = Now.AddMinutes(-30);
        (await RecordAsync(player, new AdvertisingPlayRequest(shown.Id, started, 10_000, true))).Data.Should().Be(new AdvertisingPlaysViewModel(1, 0, 0));
        var before = Logs.Entries.Count;

        // A second inside the stored play, ten plays of a second a millisecond apart, and less than a second on screen.
        var stacked = Enumerable.Range(0, 10).Select(i => new AdvertisingPlayRequest(shown.Id, started.AddSeconds(20).AddMilliseconds(i), 1_000, false));
        var batch = await RecordAsync(player, [new AdvertisingPlayRequest(shown.Id, started.AddSeconds(3), 1_000, false), .. stacked,
            new AdvertisingPlayRequest(shown.Id, started.AddSeconds(40), 999, false)]);

        batch.Data.Should().Be(new AdvertisingPlaysViewModel(1, 0, 11), "one second of the ten; the nested and the sub-second plays refused");

        // The refusal is a security event naming the display, its site and the counts, never a value of the body.
        var refused = Logs.Entries.Skip(before).Where(e => e.EventId.Id == 9126 && Equals(e.Values.GetValueOrDefault("DisplayId"), (Guid?)player.Id)).ToList();
        refused.Should().ContainSingle().Which.Should().Match<LogEntry>(e => e.Level == LogLevel.Warning && e.EventId.Name == "SecurityEvent.DisplayPlaysRefused" &&
            Equals(e.Values["SiteCode"], "DMO") && Equals(e.Values["Refused"], 11) && Equals(e.Values["Accepted"], 1));
        var written = string.Join("\n", refused.Select(e => e.Message + " " + string.Join(" ", e.Values.Values)));
        written.Should().NotContain(shown.Id.ToString()).And.NotContain(shown.Id.ToString("N")).And.NotContain(started.ToString("O", System.Globalization.CultureInfo.InvariantCulture)[..16],
            "the advertisement and the start the display sent are not logged");

        // Two batches at once claiming the same airtime: the display's lock lets one through, the other sees it stored.
        for (var round = 0; round < 5; round++)
        {
            var at = started.AddMinutes(2 + round);
            var answers = await Task.WhenAll(
                RecordAsync(player, new AdvertisingPlayRequest(shown.Id, at, 10_000, true)),
                RecordAsync(player, new AdvertisingPlayRequest(shown.Id, at.AddSeconds(2), 10_000, true)));
            answers.Sum(a => a.Data.Accepted).Should().Be(1, $"round {round}");
            answers.Sum(a => a.Data.Refused).Should().Be(1, $"round {round}");
        }

        (await _host.ReadAsync<long>("""
            SELECT count(*) FROM advertising_play a JOIN advertising_play b
                ON a.display_id = b.display_id AND a.started_utc < b.started_utc AND b.started_utc < a.started_utc + a.duration_ms * interval '1 millisecond' - interval '1 second'
             WHERE a.display_id = @id
            """, player.Id)).Should().Be(0, "no stored plays of the display were on screen together");
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_play WHERE display_id = @id", player.Id)).Should().Be(7);

        // Every play taken: no security event.
        var quiet = Logs.Entries.Count;
        (await RecordAsync(player, new AdvertisingPlayRequest(shown.Id, started.AddMinutes(10), 10_000, true))).Data.Should().Be(new AdvertisingPlaysViewModel(1, 0, 0));
        Logs.Entries.Skip(quiet).Should().NotContain(e => e.EventId.Id == 9126);
    }

    [Fact]
    public async Task Database_Should_RefuseTheRuntimeRole_When_ItChangesOrRemovesAPlay()
    {
        await using var runtime = await RuntimeAsync();
        foreach (var (reason, sql) in new[]
                 {
                     ("a play changed", "UPDATE advertising_play SET duration_ms = 1"),
                     ("a play deleted", "DELETE FROM advertising_play"),
                     ("the plays truncated", "TRUNCATE advertising_play")
                 })
        {
            await using var save = new NpgsqlCommand("SAVEPOINT play", runtime.Connection, runtime.Transaction);
            await save.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the array above
            await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be("42501", reason);
            await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT play", runtime.Connection, runtime.Transaction);
            await back.ExecuteNonQueryAsync(Ct);
        }

        foreach (var (reason, sql) in new[]
                 {
                     ("longer than any slot", "INSERT INTO advertising_play VALUES (now(), 'DMO', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 125001, true, now())"),
                     ("shorter than a second", "INSERT INTO advertising_play VALUES (now(), 'DMO', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 999, false, now())"),
                     ("received before it started", "INSERT INTO advertising_play VALUES (now(), 'DMO', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1000, true, now() - interval '6 seconds')"),
                     ("received more than a day after", "INSERT INTO advertising_play VALUES (now() - interval '25 hours', 'DMO', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1000, true, now())")
                 })
        {
            await using var save = new NpgsqlCommand("SAVEPOINT play", runtime.Connection, runtime.Transaction);
            await save.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the array above
            await using var forged = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
            var inserting = () => forged.ExecuteNonQueryAsync(Ct);
            (await inserting.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be("23514", reason);
            await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT play", runtime.Connection, runtime.Transaction);
            await back.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task Database_Should_RefuseTheRuntimeRole_When_ItChangesAPlayThroughItsChunk()
    {
        // A chunk holds the plays (a hypertable of 7 days a chunk): one is written as the owner, at the wall clock's time.
        await using (var owner = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await owner.OpenAsync(Ct);
            await using var play = new NpgsqlCommand(
                "INSERT INTO advertising_play VALUES (now(), 'DMO', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1000, true, now())", owner);
            await play.ExecuteNonQueryAsync(Ct);
        }

        await using var runtime = await RuntimeAsync();
        foreach (var (reason, sql) in new[]
                 {
                     ("a play changed through its chunk", "DO $$ BEGIN EXECUTE format('UPDATE %s SET duration_ms = 1', (SELECT c FROM show_chunks('advertising_play') AS c LIMIT 1)); END $$"),
                     ("a play deleted through its chunk", "DO $$ BEGIN EXECUTE format('DELETE FROM %s', (SELECT c FROM show_chunks('advertising_play') AS c LIMIT 1)); END $$"),
                     ("a chunk truncated", "DO $$ BEGIN EXECUTE format('TRUNCATE %s', (SELECT c FROM show_chunks('advertising_play') AS c LIMIT 1)); END $$")
                 })
        {
            await using var save = new NpgsqlCommand("SAVEPOINT chunk", runtime.Connection, runtime.Transaction);
            await save.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the array above
            await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be("42501", reason);
            await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT chunk", runtime.Connection, runtime.Transaction);
            await back.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task Figures_Should_CountThePlaysAgainstTheAirtimeThePlansScheduled_When_ADayIsRead()
    {
        var admin = await AdminAsync();
        var manager = await ManagerAsync("it.plans.figures");
        var approver = await ManagerAsync("it.plans.figures.second");
        var first = await ApprovedAsync(manager, approver);
        var second = await ApprovedAsync(manager, approver);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Counted bank", Item("Media", first.Id, 10), Item("Media", second.Id, 30)));
        var player = await PlayerAsync(admin, "PLN-FIG");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Display", ["PLN-FIG"]), Ct))).HasErrors.Should().BeFalse();
        var started = Now.AddMinutes(-10);
        (await RecordAsync(player,
                new AdvertisingPlayRequest(first.Id, started, 10_000, true),
                new AdvertisingPlayRequest(second.Id, started.AddSeconds(10), 30_000, true),
                new AdvertisingPlayRequest(first.Id, started.AddSeconds(40), 6_000, false)))
            .Data.Accepted.Should().Be(3);
        var day = new DateOnly(2026, 10, 1);

        var figures = await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", day, day, Ct));

        figures.HasErrors.Should().BeFalse(string.Join(", ", figures.ErrorMessages ?? []));
        figures.Data.TimeZone.Should().Be("Asia/Dubai");
        var rows = figures.Data.Rows.Where(r => r.DisplayCode == "PLN-FIG").ToList();
        // The whole local day (24 hours) the playlist applied to the display: a cycle of 10 + 30 seconds, so the first
        // advertisement was scheduled a quarter of it (6 hours) and the second three quarters (18 hours).
        rows.Select(r => (r.Day, r.MediaId, r.Plays, r.CompletedPlays, r.AirtimeSeconds, r.ScheduledSeconds)).Should().BeEquivalentTo(new[]
        {
            (day, first.Id, 2, 1, 16.0, 21_600.0),
            (day, second.Id, 1, 1, 30.0, 64_800.0)
        });
        rows.Single(r => r.MediaId == first.Id).Should().Match<AdvertisingFigureRowViewModel>(r =>
            r.Advertiser == "Gulf Air" && r.Campaign == "Winter sun" && r.DeliveredShare == Math.Round(16.0 / 21_600, 4));
        figures.Data.ByDisplay.Single(f => f.Key == "PLN-FIG").Should().Match<AdvertisingFigureViewModel>(f =>
            f.Plays == 3 && f.CompletedPlays == 2 && f.AirtimeSeconds == 46 && f.ScheduledSeconds == 86_400);
        figures.Data.ByDay.Should().ContainSingle().Which.Key.Should().Be("2026-10-01");
        figures.Data.ByCampaign.Select(f => f.Key).Should().Contain("Gulf Air: Winter sun");
        figures.Data.ByAdvertiser.Select(f => f.Key).Should().Contain("Gulf Air");

        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", day, day.AddDays(-1), Ct))).ErrorMessages
            .Should().Equal(AdvertisingFiguresErrors.Range);
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", day, day.AddDays(31), Ct))).ErrorMessages
            .Should().Equal(AdvertisingFiguresErrors.Range);
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", day, day.AddDays(1), Ct))).ErrorMessages
            .Should().Equal([AdvertisingFiguresErrors.Range], "2 October has not begun at DMO");
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", DateOnly.MinValue, DateOnly.MinValue, Ct))).ErrorMessages
            .Should().Equal([AdvertisingFiguresErrors.Range], "older than any play is kept");
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", DateOnly.MaxValue, DateOnly.MaxValue, Ct))).ErrorMessages
            .Should().Equal([AdvertisingFiguresErrors.Range]);
        var outsider = await ManagerAsync("it.plans.figures.outsider", "PLN2");
        (await _host.AsCallerAsync(outsider, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", day, day, Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.NotFound);
        foreach (var (name, temporary, disabled) in new[] { ("it.plans.figures.disabled", false, true), ("it.plans.figures.pending", true, false) })
        {
            var refusedUser = await _host.CreateUserAsync(name, temporary: temporary, disabled: disabled, roles: [RoleCodes.TerminalDutyManager]);
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(refusedUser, new SiteAccessRequest(false, ["DMO"]), Ct));
            (await _host.AsCallerAsync(refusedUser, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", day, day, Ct))).ErrorMessages
                .Should().Equal([TopologyErrors.NotFound], disabled ? "a disabled account of the site's duty managers" : "a pending account (its password still to change)");
        }

        var border = await _host.CreateUserAsync("it.plans.figures.border", roles: [RoleCodes.BorderShiftSupervisor]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(border, new SiteAccessRequest(false, ["DMO"]), Ct));
        (await _host.AsCallerAsync(border, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("DMO", day, day, Ct))).ErrorMessages
            .Should().Equal([TopologyErrors.NotFound], "a role of the site that does not view advertising, answered as a site that does not exist");
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingFigures>().ReadAsync("<script>", day, day, Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.NotFound);
    }

    public static TheoryData<string, string> RuntimeRefusals => new()
    {
        { "another site's advertisement", "UPDATE advertising_playlist SET items = '[{\"kind\":\"Media\",\"id\":\"' || (SELECT id FROM advertising_media WHERE site_code = 'PLN2' LIMIT 1) || '\",\"seconds\":10,\"weight\":1}]' WHERE id = @id" },
        { "an id that is no item", "UPDATE advertising_playlist SET items = '[{\"kind\":\"Media\",\"id\":\"' || gen_random_uuid() || '\",\"seconds\":10,\"weight\":1}]' WHERE id = @id" },
        { "a kind that is not taken", "UPDATE advertising_playlist SET items = replace(items, '\"Media\"', '\"Video\"') WHERE id = @id" },
        { "no time on screen", "UPDATE advertising_playlist SET items = replace(items, '\"seconds\":10', '\"seconds\":0') WHERE id = @id" },
        { "a weight of 11", "UPDATE advertising_playlist SET items = replace(items, '\"weight\":1', '\"weight\":11') WHERE id = @id" },
        { "a fraction of a second", "UPDATE advertising_playlist SET items = replace(items, '\"seconds\":10', '\"seconds\":10.5') WHERE id = @id" },
        { "no items", "UPDATE advertising_playlist SET items = '[]' WHERE id = @id" },
        { "items that are not a list", "UPDATE advertising_playlist SET items = '{\"kind\":\"Media\"}' WHERE id = @id" },
        { "moved to another site", "UPDATE advertising_playlist SET site_code = 'PLN2' WHERE id = @id" },
        { "a status that is not taken", "UPDATE advertising_playlist SET status = 'Deleted' WHERE id = @id" },
        { "a schedule of another site's playlist", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                                  "SELECT gen_random_uuid(), 'PLN2', id, 'Site', '[]', 'Monday', 0, 1440, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a schedule for the site naming a zone", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                                    "SELECT gen_random_uuid(), site_code, id, 'Site', '[\"A-CIT\"]', 'Monday', 0, 1440, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a day that is not a day", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                     "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Funday', 0, 1440, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a window opening at 24:00", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                       "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Monday', 1440, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "zone targets that are not a list", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                              "SELECT gen_random_uuid(), site_code, id, 'Zone', '{\"zone\":\"A-CIT\"}', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a zone target that is not text", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                            "SELECT gen_random_uuid(), site_code, id, 'Zone', '[1]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "an empty zone target", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                  "SELECT gen_random_uuid(), site_code, id, 'Zone', '[\"\"]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a zone named twice", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                "SELECT gen_random_uuid(), site_code, id, 'Zone', '[\"A-CIT\",\"A-CIT\"]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "21 displays", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                         "SELECT gen_random_uuid(), site_code, id, 'Display', (SELECT json_agg('D-' || n)::text FROM generate_series(1, 21) n), 'Monday', 0, 60, 'Active' " +
                         "FROM advertising_playlist WHERE id = @id" },
        { "a zone schedule naming an empty list written with a space", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                                                       "SELECT gen_random_uuid(), site_code, id, 'Zone', '[ ]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "targets that are not JSON", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                       "SELECT gen_random_uuid(), site_code, id, 'Zone', 'A-CIT', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a schedule moved to another site", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                              "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id; " +
                                              "UPDATE advertising_schedule SET site_code = 'PLN2' WHERE playlist_id = @id" },
        { "a schedule given another id", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                         "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id; " +
                                         "UPDATE advertising_schedule SET id = gen_random_uuid() WHERE playlist_id = @id" }
    };

    [Theory]
    [MemberData(nameof(RuntimeRefusals))]
    public async Task Database_Should_RefuseTheRuntimeRole_When_AWriteBreaksTheRules(string reason, string sql)
    {
        var manager = await ManagerAsync($"it.plans.rt.{Math.Abs(reason.GetHashCode(StringComparison.Ordinal)) % 100000}", "DMO", "PLN2");
        await MediaAsync(manager, "PLN2");
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Runtime", Item("Media", (await MediaAsync(manager)).Id)));

        await using var runtime = await RuntimeAsync();
#pragma warning disable CA2100 // literal statements from the theory data above
        await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", playlist.Id);
        var writing = () => command.ExecuteNonQueryAsync(Ct);

        (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().BeOneOf(["23514", "23503", "22P02"], reason);
    }

    [Fact]
    public async Task Database_Should_RefuseDeletesAndKeepRetiredItemsInAPlaylist_When_TheRuntimeRoleWrites()
    {
        var manager = await ManagerAsync("it.plans.rt.kept");
        var media = await MediaAsync(manager);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Kept", Item("Media", media.Id)));
        var schedule = (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id), Ct))).Data;

        foreach (var (reason, sql, id) in new (string, string, Guid)[]
                 {
                     ("a playlist deleted", "DELETE FROM advertising_playlist WHERE id = @id", playlist.Id),
                     ("a schedule deleted", "DELETE FROM advertising_schedule WHERE id = @id", schedule.Id),
                     ("the schedules truncated", "TRUNCATE advertising_schedule", schedule.Id),
                     ("the playlists truncated", "TRUNCATE advertising_playlist CASCADE", playlist.Id)
                 })
        {
            await using var runtime = await RuntimeAsync();
#pragma warning disable CA2100 // literal statements from the array above
            await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("id", id);
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be("42501", reason);
        }

        // An item retired after the playlist was written leaves the playlist as it was: it can still be renamed and retired.
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingMedia>().RetireAsync("DMO", media.Id, Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", playlist.Id, Ct))).Data.Status.Should().Be("Retired");
        (await _host.AsCallerAsync(manager, s => Playlists(s).GetAsync("DMO", playlist.Id, Ct))).Data.Items.Should().ContainSingle()
            .Which.Should().Match<AdvertisingPlaylistItemViewModel>(i => i.MediaStatus == "Retired" && !i.Playable);
    }

    [Fact]
    public async Task Database_Should_RefuseASlideOfAnotherSiteOrARetiredOne_When_TheRuntimeRoleWritesAPlaylist()
    {
        var admin = await AdminAsync();
        var manager = await ManagerAsync("it.plans.rt.slides");
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Slides", Item("Media", (await MediaAsync(manager)).Id)));
        var theirs = await SlideAsync(admin, "PLN2", "Another site's slide");
        var retired = await SlideAsync(admin, title: "A retired slide");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcInstructionSlides>().RetireAsync(retired, Ct))).HasErrors.Should().BeFalse();

        foreach (var (reason, slide) in new[] { ("another site's slide", theirs), ("a retired slide", retired) })
        {
            await using var runtime = await RuntimeAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE advertising_playlist SET items = '[{\"kind\":\"Slide\",\"id\":\"' || @slide::text || '\",\"seconds\":10,\"weight\":1}]' WHERE id = @id",
                runtime.Connection, runtime.Transaction);
            command.Parameters.AddWithValue("slide", slide);
            command.Parameters.AddWithValue("id", playlist.Id);
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be("23514", reason);
        }
    }

    private sealed class Runtime(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A connection with an open transaction as the runtime role (rolled back when disposed).</summary>
    private async Task<Runtime> RuntimeAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        return new Runtime(connection, transaction);
    }

    /// <summary>A log entry as written: its event, level, message and structured values.</summary>
    private sealed record LogEntry(EventId EventId, LogLevel Level, string Message, IReadOnlyDictionary<string, object> Values);

    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object>> pairs
                ? pairs.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal)
                : new Dictionary<string, object>(StringComparer.Ordinal);
            lock (_entries)
                _entries.Add(new LogEntry(eventId, logLevel, formatter(state, exception), values));
        }

        public void Dispose()
        {
        }
    }
}
